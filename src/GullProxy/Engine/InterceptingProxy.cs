using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using GullProxy.Capture;
using GullProxy.Proxy;
using GullProxy.Tls;

namespace GullProxy.Engine;

/// <summary>
/// An explicit HTTP/HTTPS forward proxy that intercepts and records traffic. It terminates TLS
/// from the client using per-host leaf certs (advertising HTTP/1.1 so browsers speak a protocol
/// we can read), and forwards each request through a shared <see cref="HttpClient"/> that
/// negotiates HTTP/1.1, HTTP/2, or HTTP/3 with the real origin. Bodies are streamed (never fully
/// buffered on the response path) so downloads and streaming endpoints work, and any failure
/// returns a fast 502 instead of hanging — so one broken site never freezes the rest.
/// </summary>
public sealed class InterceptingProxy : IAsyncDisposable
{
    private readonly int _port;
    private readonly LeafCertCache _leaves;
    private readonly TransactionStore _store;
    private readonly HttpMessageInvoker _upstream;
    private readonly SystemIntegration.ProcessResolver _procs = new();
    private TcpListener? _listener;

    private const int TeeCap = 1024 * 1024; // keep up to 1 MB of each body for display

    public InterceptingProxy(int port, LeafCertCache leaves, TransactionStore store)
    {
        _port = port;
        _leaves = leaves;
        _store = store;

        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None, // forward encoded bytes untouched
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
            ResponseDrainTimeout = TimeSpan.FromSeconds(5),
        };
        _upstream = new HttpMessageInvoker(handler);
    }

    public int Port => _port;

    public async Task RunAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Loopback, _port);
        _listener.Start();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }

                _ = Task.Run(() => SafeHandle(client, ct), ct);
            }
        }
        finally { _listener.Stop(); }
    }

    private async Task SafeHandle(TcpClient client, CancellationToken ct)
    {
        try { await HandleConnection(client, ct).ConfigureAwait(false); }
        catch { /* never let a connection crash the app */ }
        finally { try { client.Close(); } catch { } }
    }

    private async Task HandleConnection(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        Stream stream = client.GetStream();
        string clientEp = client.Client.RemoteEndPoint?.ToString() ?? "?";

        var first = await HttpParser.ReadHeadAsync(stream, ct).ConfigureAwait(false);
        if (first is null) return;
        var head = HttpParser.ParseRequestHead(first.Value.head);

        if (head.Method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            await HandleConnect(stream, head, clientEp, ct).ConfigureAwait(false);
        }
        else
        {
            await ServeHttp(stream, head, first.Value.leftover, scheme: "http",
                fixedHost: null, fixedPort: 0, clientEp, isTls: false, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleConnect(Stream clientStream, RequestHead head, string clientEp, CancellationToken ct)
    {
        var (host, port) = SplitAuthority(head.Target, 443);

        await clientStream.WriteAsync(
            Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct).ConfigureAwait(false);
        await clientStream.FlushAsync(ct).ConfigureAwait(false);

        X509Certificate2 leaf;
        try { leaf = _leaves.Get(host); }
        catch { return; }

        var tls = new SslStream(clientStream, leaveInnerStreamOpen: false);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = leaf,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                // Advertise HTTP/1.1 only so the client uses a protocol we can parse. The origin
                // leg still uses HTTP/2/3 via HttpClient.
                ApplicationProtocols = new List<SslApplicationProtocol> { SslApplicationProtocol.Http11 },
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException)
        {
            tls.Dispose();
            return;
        }

        try
        {
            var head2 = await HttpParser.ReadHeadAsync(tls, ct).ConfigureAwait(false);
            if (head2 is null) return;
            var req = HttpParser.ParseRequestHead(head2.Value.head);
            await ServeHttp(tls, req, head2.Value.leftover, scheme: "https",
                fixedHost: host, fixedPort: port, clientEp, isTls: true, ct).ConfigureAwait(false);
        }
        finally { tls.Dispose(); }
    }

    /// <summary>Handles a keep-alive sequence of HTTP/1.1 requests on one (already-decrypted) stream.</summary>
    private async Task ServeHttp(Stream stream, RequestHead head, byte[] leftover, string scheme,
        string? fixedHost, int fixedPort, string clientEp, bool isTls, CancellationToken ct)
    {
        while (true)
        {
            string host; int port; string path;
            if (fixedHost is not null)
            {
                host = fixedHost; port = fixedPort; path = head.Target;
            }
            else
            {
                var parsed = SplitAbsoluteTarget(head.Target);
                if (parsed.host is null)
                {
                    await WriteError(stream, 400, "Bad Request", "Expected absolute-form URI.", ct).ConfigureAwait(false);
                    return;
                }
                host = parsed.host; port = parsed.port; path = parsed.path; scheme = "http";
            }

            // WebSocket / protocol upgrades can't go through HttpClient — relay them raw so they
            // keep working (frames aren't decoded, but the connection is logged).
            if (HasUpgrade(head))
            {
                await RawRelay(stream, head, leftover, host, port, path, clientEp, isTls, ct).ConfigureAwait(false);
                return;
            }

            bool keepAlive = await ForwardOnce(stream, head, leftover, scheme, host, port, path, clientEp, isTls, ct)
                .ConfigureAwait(false);
            if (!keepAlive) return;

            var next = await HttpParser.ReadHeadAsync(stream, ct).ConfigureAwait(false);
            if (next is null) return;
            head = HttpParser.ParseRequestHead(next.Value.head);
            leftover = next.Value.leftover;
        }
    }

    private async Task<bool> ForwardOnce(Stream stream, RequestHead head, byte[] leftover, string scheme,
        string host, int port, string path, string clientEp, bool isTls, CancellationToken ct)
    {
        var tx = new Transaction
        {
            Id = _store.NextId(),
            ClientEndpoint = clientEp,
            Method = head.Method,
            Scheme = scheme,
            Host = host,
            Port = port,
            Path = path,
            HttpVersion = head.Version,
            IsTls = isTls,
        };
        ResolveApp(tx, clientEp);
        foreach (var h in head.Headers) tx.RequestHeaders.Add(h);
        var sw = Stopwatch.StartNew();

        // Read the request body (buffered — requests are usually small).
        byte[] reqBody = await HttpParser.ReadBodyAsync(stream, leftover, head.Header("Content-Length"),
            head.Header("Transfer-Encoding"), bodyAllowed: false, ct).ConfigureAwait(false);
        var (reqTee, reqTrunc) = Cap(reqBody);
        BodyCapture.RecordRequestBody(tx, reqTee, reqBody.LongLength, reqTrunc, head.Header("Content-Encoding"));

        string url = $"{scheme}://{host}{(IsDefaultPort(scheme, port) ? "" : ":" + port)}{path}";
        using var request = new HttpRequestMessage(new HttpMethod(head.Method), url)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
        ApplyRequestHeaders(request, head, reqBody);

        HttpResponseMessage response;
        try
        {
            response = await _upstream.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            tx.Error = ex.InnerException?.Message ?? ex.Message;
            tx.Duration = sw.Elapsed;
            _store.Add(tx);
            await WriteError(stream, 502, "Bad Gateway", $"GullProxy could not reach {host}: {tx.Error}", ct)
                .ConfigureAwait(false);
            return false; // close after an error
        }

        using (response)
        {
            tx.TtfbMs = sw.Elapsed.TotalMilliseconds; // response headers have arrived
            tx.StatusCode = (int)response.StatusCode;
            tx.StatusText = response.ReasonPhrase ?? "";
            tx.HttpVersion = "HTTP/" + response.Version;
            foreach (var (k, v) in Flatten(response.Headers)) tx.ResponseHeaders.Add(new(k, v));
            if (response.Content is not null)
                foreach (var (k, v) in Flatten(response.Content.Headers)) tx.ResponseHeaders.Add(new(k, v));

            string? respEncoding = Transaction.HeaderValue(tx.ResponseHeaders, "Content-Encoding");
            bool bodyAllowed = ResponseCanHaveBody(tx.StatusCode, head.Method);

            bool keepAlive = await WriteResponse(stream, tx, response, bodyAllowed, respEncoding, ct)
                .ConfigureAwait(false);
            tx.Duration = sw.Elapsed;
            _store.Add(tx);
            return keepAlive;
        }
    }

    /// <summary>Streams the response to the client using chunked framing while tee-ing a capped copy.</summary>
    private async Task<bool> WriteResponse(Stream client, Transaction tx, HttpResponseMessage response,
        bool bodyAllowed, string? contentEncoding, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ')
          .Append(response.ReasonPhrase ?? "").Append("\r\n");
        foreach (var (k, v) in tx.ResponseHeaders)
        {
            if (IsHopByHop(k) || k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || k.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(k).Append(": ").Append(v).Append("\r\n");
        }
        sb.Append("Connection: keep-alive\r\n");
        if (bodyAllowed) sb.Append("Transfer-Encoding: chunked\r\n");
        else sb.Append("Content-Length: 0\r\n");
        sb.Append("\r\n");
        await client.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);

        if (!bodyAllowed)
        {
            await client.FlushAsync(ct).ConfigureAwait(false);
            BodyCapture.RecordResponseBody(tx, Array.Empty<byte>(), 0, false, contentEncoding);
            return true;
        }

        long total = 0;
        using var tee = new MemoryStream();
        var buffer = new byte[64 * 1024];
        await using (var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            while (true)
            {
                int n;
                try { n = await body.ReadAsync(buffer, ct).ConfigureAwait(false); }
                catch { break; } // origin dropped — stop cleanly
                if (n == 0) break;
                total += n;
                if (tee.Length < TeeCap)
                    tee.Write(buffer, 0, (int)Math.Min(n, TeeCap - tee.Length));

                // chunk: <hex-size>CRLF <data> CRLF
                await client.WriteAsync(Encoding.ASCII.GetBytes(n.ToString("x") + "\r\n"), ct).ConfigureAwait(false);
                await client.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await client.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            }
        }
        await client.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        await client.FlushAsync(ct).ConfigureAwait(false);

        BodyCapture.RecordResponseBody(tx, tee.ToArray(), total, total > TeeCap, contentEncoding);
        return true;
    }

    /// <summary>Raw bidirectional relay for WebSocket / Upgrade requests, logged as a captured row.</summary>
    private async Task RawRelay(Stream client, RequestHead head, byte[] leftover, string host, int port,
        string path, string clientEp, bool tls, CancellationToken ct)
    {
        // Surface the connection in the UI even though we don't decode frames.
        var tx = new Transaction
        {
            Id = _store.NextId(),
            ClientEndpoint = clientEp,
            Method = head.Method,
            Scheme = tls ? "wss" : "ws",
            Host = host,
            Port = port,
            Path = path,
            HttpVersion = head.Version,
            IsTls = tls,
            IsWebSocket = true,
            StatusCode = 101,
            StatusText = "Switching Protocols",
        };
        ResolveApp(tx, clientEp);
        foreach (var h in head.Headers) tx.RequestHeaders.Add(h);
        var sw = Stopwatch.StartNew();
        _store.Add(tx);

        try
        {
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(host, port, ct).ConfigureAwait(false);
            upstream.NoDelay = true;
            Stream up = upstream.GetStream();
            if (tls)
            {
                var upTls = new SslStream(up, false);
                await upTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct)
                    .ConfigureAwait(false);
                up = upTls;
            }
            await up.WriteAsync(HttpParser.SerializeRequestHead(head), ct).ConfigureAwait(false);
            if (leftover.Length > 0) await up.WriteAsync(leftover, ct).ConfigureAwait(false);
            await up.FlushAsync(ct).ConfigureAwait(false);

            var a = client.CopyToAsync(up, ct);
            var b = up.CopyToAsync(client, ct);
            await Task.WhenAny(a, b).ConfigureAwait(false);
        }
        catch { /* upgrade relay best-effort */ }
        finally { tx.Duration = sw.Elapsed; }
    }

    // ---- Header helpers --------------------------------------------------------------------

    private static void ApplyRequestHeaders(HttpRequestMessage request, RequestHead head, byte[] body)
    {
        HttpContent? content = null;
        if (body.Length > 0 || MethodUsuallyHasBody(head.Method))
            content = new ByteArrayContent(body);

        foreach (var h in head.Headers)
        {
            if (IsHopByHop(h.Key)) continue;
            if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (request.Headers.TryAddWithoutValidation(h.Key, h.Value)) continue;
            content ??= new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        request.Content = content;
    }

    private static IEnumerable<(string, string)> Flatten(HttpHeaders headers)
    {
        foreach (var h in headers)
            foreach (var v in h.Value)
                yield return (h.Key, v);
    }

    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };
    private static bool IsHopByHop(string name) => HopByHop.Contains(name);

    private static bool HasUpgrade(RequestHead head)
    {
        var u = head.Header("Upgrade");
        return !string.IsNullOrEmpty(u);
    }

    private static bool MethodUsuallyHasBody(string method) =>
        method.Equals("POST", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PUT", StringComparison.OrdinalIgnoreCase)
        || method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);

    private static bool ResponseCanHaveBody(int status, string method)
    {
        if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)) return false;
        if (status is >= 100 and < 200) return false;
        return status is not (204 or 304);
    }

    private static (byte[] tee, bool truncated) Cap(byte[] body) =>
        body.Length <= TeeCap ? (body, false) : (body.AsSpan(0, TeeCap).ToArray(), true);

    private static bool IsDefaultPort(string scheme, int port) =>
        (scheme == "http" && port == 80) || (scheme == "https" && port == 443);

    private static (string? host, int port, string path) SplitAbsoluteTarget(string target)
    {
        int s = target.IndexOf("://", StringComparison.Ordinal);
        if (s < 0) return (null, 0, target);
        string rest = target[(s + 3)..];
        int slash = rest.IndexOf('/');
        string authority = slash < 0 ? rest : rest[..slash];
        string path = slash < 0 ? "/" : rest[slash..];
        var (host, port) = SplitAuthority(authority, 80);
        return (host, port, path);
    }

    private static (string host, int port) SplitAuthority(string authority, int defaultPort)
    {
        int colon = authority.LastIndexOf(':');
        if (colon > 0 && int.TryParse(authority[(colon + 1)..], out int p)) return (authority[..colon], p);
        return (authority, defaultPort);
    }

    private static async Task WriteError(Stream s, int code, string reason, string message, CancellationToken ct)
    {
        try
        {
            byte[] payload = Encoding.UTF8.GetBytes(message);
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: text/plain; charset=utf-8\r\n");
            sb.Append("Content-Length: ").Append(payload.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");
            await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
            await s.WriteAsync(payload, ct).ConfigureAwait(false);
            await s.FlushAsync(ct).ConfigureAwait(false);
        }
        catch { /* client already gone */ }
    }

    private void ResolveApp(Transaction tx, string clientEp)
    {
        int colon = clientEp.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(clientEp[(colon + 1)..], out int clientPort)) return;
        var app = _procs.Resolve(clientPort, _port);
        if (app is { } a) { tx.App = a.Name; tx.Pid = a.Pid; }
    }

    public ValueTask DisposeAsync()
    {
        _upstream.Dispose();
        return ValueTask.CompletedTask;
    }
}
