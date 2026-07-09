using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using GullProxy.Capture;

namespace GullProxy.Ui;

/// <summary>
/// "Talon" — a request editor. You write a whole request in <see cref="TalonFormat"/> (method,
/// URL, headers, body, {{variables}}) in a single code editor, and can embed <b>TalonScript</b>
/// in <c>&lt; {% %}</c> (pre-request) and <c>&gt; {% %}</c> (post-response) blocks to compute
/// values, extract data from responses, and drive one request from another. Variables set by a
/// script persist across sends (session variables), so e.g. a login response can hand a token to
/// the next request. Requests go straight to the origin, bypassing the capture proxy.
/// </summary>
public sealed class TalonViewModel : INotifyPropertyChanged
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, object?> _sessionVars = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxScriptBody = 4 * 1024 * 1024;

    public const string Template =
        "# TalonFormat — write a request, use {{variables}}, script with < {% %} / > {% %}.\n" +
        "@host = https://httpbin.org\n" +
        "\n" +
        "POST {{host}}/post\n" +
        "Content-Type: application/json\n" +
        "\n" +
        "{ \"hello\": \"world\" }\n" +
        "\n" +
        "> {%\n" +
        "  # TalonScript runs after the response. Save values into vars to reuse as {{...}}.\n" +
        "  log \"status:\", response.status\n" +
        "  echoed = response.json().json.hello\n" +
        "  log \"server echoed:\", echoed\n" +
        "%}\n";

    public TalonViewModel()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            AllowAutoRedirect = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        SendCommand = new RelayCommand(async () => await SendAsync(), () => !IsSending && HasRequest);
        CopyTalonFormatCommand = new RelayCommand(() => Copy(TalonText));
        CopyCurlCommand = new RelayCommand(CopyCurl);
        CopyResponseHeadersCommand = new RelayCommand(() => Copy(ResponseHeaders));
        CopyResponseBodyCommand = new RelayCommand(() => Copy(ResponseBody));
    }

    public RelayCommand SendCommand { get; }
    public RelayCommand CopyTalonFormatCommand { get; }
    public RelayCommand CopyCurlCommand { get; }
    public RelayCommand CopyResponseHeadersCommand { get; }
    public RelayCommand CopyResponseBodyCommand { get; }

    private string _talonText = Template;
    public string TalonText
    {
        get => _talonText;
        set { _talonText = value; Raise(); SendCommand.RaiseCanExecuteChanged(); }
    }

    private bool HasRequest => !string.IsNullOrWhiteSpace(TalonFormat.Parse(TalonText).Url);

    private bool _isSending;
    public bool IsSending
    {
        get => _isSending;
        private set { _isSending = value; Raise(); Raise(nameof(SendButtonText)); SendCommand.RaiseCanExecuteChanged(); }
    }
    public string SendButtonText => IsSending ? "Sending…" : "▶  Send";

    private string _responseStatus = "";
    public string ResponseStatus { get => _responseStatus; private set { _responseStatus = value; Raise(); } }

    private Brush _responseStatusBrush = Brushes.Gray;
    public Brush ResponseStatusBrush { get => _responseStatusBrush; private set { _responseStatusBrush = value; Raise(); } }

    private string _responseHeaders = "";
    public string ResponseHeaders { get => _responseHeaders; private set { _responseHeaders = value; Raise(); } }

    private string _responseBody = "";
    public string ResponseBody { get => _responseBody; private set { _responseBody = value; Raise(); } }

    private string _consoleOutput = "";
    public string ConsoleOutput { get => _consoleOutput; private set { _consoleOutput = value; Raise(); } }

    public void LoadFrom(Transaction tx)
    {
        TalonText = TalonFormat.FromTransaction(tx);
        ResponseStatus = ResponseHeaders = ResponseBody = ConsoleOutput = "";
        ResponseStatusBrush = Brushes.Gray;
    }

    private async Task SendAsync()
    {
        IsSending = true;
        ResponseStatus = "Sending…";
        ResponseStatusBrush = Brushes.Gray;
        ResponseHeaders = ResponseBody = ConsoleOutput = "";
        var sw = Stopwatch.StartNew();

        try
        {
            var p = TalonFormat.Parse(TalonText);

            // variable scope: session vars, then this document's @vars on top
            var vars = new Dictionary<string, object?>(_sessionVars, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in p.Vars) vars[kv.Key] = kv.Value;

            var script = new TalonScript(vars);

            // ---- pre-request script (can rewrite request.* and set vars) ----
            var headers = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in p.Headers) headers[h.Key] = h.Value;
            var requestObj = new Dictionary<string, object?>
            {
                ["method"] = p.Method, ["url"] = p.Url, ["headers"] = headers, ["body"] = p.Body,
            };
            script.SetGlobal("request", requestObj);
            if (p.PreScript.Length > 0) script.Run(p.PreScript);

            string method = TalonScript.Stringify(requestObj["method"]);
            string url = TalonScript.Stringify(requestObj["url"]);
            string body = requestObj["body"] is null ? "" : TalonScript.Stringify(requestObj["body"]);
            headers = requestObj["headers"] as Dictionary<string, object?> ?? headers;

            // resolve relative URL + substitute {{vars}}
            var strVars = StringVars(vars);
            if (!url.Contains("://", StringComparison.Ordinal) &&
                headers.TryGetValue("Host", out var hostVal) && hostVal is not null)
                url = "https://" + TalonScript.Stringify(hostVal) + (url.StartsWith('/') ? url : "/" + url);
            url = TalonFormat.Sub(url, strVars);
            body = TalonFormat.Sub(body, strVars);

            using var req = new HttpRequestMessage(new HttpMethod(method.Trim().ToUpperInvariant()), url)
            {
                Version = System.Net.HttpVersion.Version20,
                VersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrLower,
            };
            HttpContent? content = string.IsNullOrEmpty(body) ? null : new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            foreach (var (k, raw) in headers)
            {
                if (k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                string v = TalonFormat.Sub(TalonScript.Stringify(raw), strVars);
                if (req.Headers.TryAddWithoutValidation(k, v)) continue;
                content ??= new ByteArrayContent(Array.Empty<byte>());
                content.Headers.TryAddWithoutValidation(k, v);
            }
            req.Content = content;

            using var resp = await _http.SendAsync(req).ConfigureAwait(true);
            sw.Stop();

            int status = (int)resp.StatusCode;
            ResponseStatus = $"{status} {resp.ReasonPhrase}   ·   HTTP/{resp.Version}   ·   {sw.ElapsedMilliseconds} ms";
            ResponseStatusBrush = StatusBrush(status);

            var hb = new StringBuilder();
            var respHeaders = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in resp.Headers) foreach (var v in h.Value) { hb.Append(h.Key).Append(": ").Append(v).Append('\n'); respHeaders[h.Key] = v; }
            foreach (var h in resp.Content.Headers) foreach (var v in h.Value) { hb.Append(h.Key).Append(": ").Append(v).Append('\n'); respHeaders[h.Key] = v; }
            ResponseHeaders = hb.ToString().TrimEnd('\n');

            byte[] bodyBytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(true);
            ResponseBody = BodyView.Format(bodyBytes, resp.Content.Headers.ContentType?.ToString());
            string respText = Encoding.UTF8.GetString(bodyBytes, 0, Math.Min(bodyBytes.Length, MaxScriptBody));

            // ---- post-response script (can read response.* and set vars) ----
            if (p.PostScript.Length > 0)
            {
                var responseObj = new Dictionary<string, object?>
                {
                    ["status"] = (double)status,
                    ["statusText"] = resp.ReasonPhrase ?? "",
                    ["headers"] = respHeaders,
                    ["body"] = respText,
                    ["json"] = (Func<object?[], object?>)(_ => TalonScript.JsonToValue(respText)),
                };
                script.SetGlobal("response", responseObj);
                script.Run(p.PostScript);
            }

            // persist variables for the next send
            _sessionVars.Clear();
            foreach (var kv in vars) _sessionVars[kv.Key] = kv.Value;

            if (script.Output.Count > 0) ConsoleOutput = string.Join('\n', script.Output);
        }
        catch (TalonError te)
        {
            ResponseStatus = "Script error";
            ResponseStatusBrush = StatusBrush(500);
            ConsoleOutput = "⚠ " + te.Message;
        }
        catch (Exception ex)
        {
            ResponseStatus = "Error";
            ResponseStatusBrush = StatusBrush(500);
            ResponseBody = ex.InnerException?.Message ?? ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    private void CopyCurl()
    {
        var p = TalonFormat.Parse(TalonText);
        TalonFormat.ResolveRelativeUrl(p);
        var strVars = new Dictionary<string, string>(p.Vars, StringComparer.OrdinalIgnoreCase);
        TalonFormat.ApplyVariables(p, strVars);
        Copy(TalonFormat.ToCurl(p));
    }

    private static Dictionary<string, string> StringVars(Dictionary<string, object?> vars)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in vars) d[kv.Key] = TalonScript.Stringify(kv.Value);
        return d;
    }

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { }
    }

    private static Brush StatusBrush(int status)
    {
        var c = status switch
        {
            >= 500 => Color.FromRgb(0xF0, 0x61, 0x6B),
            >= 400 => Color.FromRgb(0xE2, 0xA0, 0x3F),
            >= 300 => Color.FromRgb(0xD7, 0xC4, 0x4C),
            >= 200 => Color.FromRgb(0x5B, 0xC8, 0x8A),
            _ => Color.FromRgb(0x8A, 0x90, 0x9C),
        };
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
