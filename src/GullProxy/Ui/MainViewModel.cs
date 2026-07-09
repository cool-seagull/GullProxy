using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using GullProxy.Capture;
using GullProxy.Net;
using GullProxy.SystemIntegration;

namespace GullProxy.Ui;

/// <summary>Drives the main window: the live request list, filtering, detail pane, and the
/// capture on/off switch (which applies or restores the Windows system proxy).</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private const int MaxRows = 5000;

    private readonly TransactionStore _store;
    private readonly SystemProxy _systemProxy;
    private readonly int _port;
    private readonly GeoLocator _geo = new();
    private readonly FlagProvider _flags = new();

    public ObservableCollection<RequestRow> Rows { get; } = new();
    public ICollectionView RowsView { get; }

    public RelayCommand ToggleCaptureCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyUrlCommand { get; }
    public RelayCommand CopyCurlCommand { get; }
    public RelayCommand CopyResponseBodyCommand { get; }
    public RelayCommand CopyRequestHeadersCommand { get; }
    public RelayCommand CopyResponseHeadersCommand { get; }
    public RelayCommand CopyRequestBodyCommand { get; }
    public RelayCommand SendToTalonCommand { get; }
    public RelayCommand CopyTalonFormatCommand { get; }
    public RelayCommand CopyTalonScriptCommand { get; }
    public RelayCommand SaveResponseBodyCommand { get; }
    public RelayCommand SetStatusFilterCommand { get; }

    public TalonViewModel Talon { get; } = new();

    private bool _autoScroll = true;
    public bool AutoScroll { get => _autoScroll; set { _autoScroll = value; OnChanged(); } }

    private bool _wrapBodies = true;
    public bool WrapBodies { get => _wrapBodies; set { _wrapBodies = value; OnChanged(); } }

    private string _statusFilter = "All";
    public string[] StatusFilters { get; } = { "All", "2xx", "3xx", "4xx", "5xx", "ERR" };

    public string CertScopeText { get; }

    private int _selectedTab;
    public int SelectedTab { get => _selectedTab; set { _selectedTab = value; OnChanged(); } }

    public MainViewModel(TransactionStore store, SystemProxy systemProxy, int port, string certScopeText)
    {
        _store = store;
        _systemProxy = systemProxy;
        _port = port;
        CertScopeText = certScopeText;

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = FilterRow;

        ToggleCaptureCommand = new RelayCommand(ToggleCapture);
        ClearCommand = new RelayCommand(Clear);
        CopyUrlCommand = new RelayCommand(() => Copy(_selected?.Url));
        CopyCurlCommand = new RelayCommand(() => Copy(_selected?.ToCurl()));
        CopyResponseBodyCommand = new RelayCommand(() => Copy(_selected is null ? null : ResponseBodyText));
        CopyRequestHeadersCommand = new RelayCommand(() => Copy(_selected is null ? null : RequestHeadersText));
        CopyResponseHeadersCommand = new RelayCommand(() => Copy(_selected is null ? null : ResponseHeadersText));
        CopyRequestBodyCommand = new RelayCommand(() => Copy(_selected is null ? null : RequestBodyText));
        SendToTalonCommand = new RelayCommand(SendToTalon);
        CopyTalonFormatCommand = new RelayCommand(() => Copy(_selected?.ToTalonFormat()));
        CopyTalonScriptCommand = new RelayCommand(() => Copy(_selected?.ToTalonScript()));
        SaveResponseBodyCommand = new RelayCommand(SaveResponseBody);
        SetStatusFilterCommand = new RelayCommand(p => { _statusFilter = p as string ?? "All"; RowsView.Refresh(); });

        _ = DrainAsync();
    }

    public bool GeoEnabled
    {
        get => _geo.Enabled;
        set { _geo.Enabled = value; OnChanged(); }
    }

    private static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }

    // ---- Capture switch --------------------------------------------------------------------

    private bool _capturing;
    public bool IsCapturing
    {
        get => _capturing;
        private set { _capturing = value; OnChanged(); OnChanged(nameof(CaptureButtonText)); OnChanged(nameof(StatusText)); }
    }

    public string CaptureButtonText => IsCapturing ? "■  Stop capturing" : "●  Start capturing";

    public string StatusText => IsCapturing
        ? $"Capturing · system proxy → 127.0.0.1:{_port} · {CertScopeText}"
        : $"Idle · system proxy restored · {CertScopeText}";

    public void StartCapture()
    {
        if (IsCapturing) return;
        _systemProxy.Apply(_port);
        IsCapturing = true;
    }

    public void StopCapture()
    {
        if (!IsCapturing) return;
        SystemProxy.Restore();
        IsCapturing = false;
    }

    private void ToggleCapture()
    {
        if (IsCapturing) StopCapture(); else StartCapture();
    }

    // ---- Live feed -------------------------------------------------------------------------

    private async Task DrainAsync()
    {
        try
        {
            await foreach (var tx in _store.Live.ReadAllAsync())
            {
                var row = new RequestRow(tx);
                await Application.Current.Dispatcher.InvokeAsync(() => AddRow(row));
            }
        }
        catch { /* app shutting down */ }
    }

    private void AddRow(RequestRow row)
    {
        Rows.Add(row);
        if (Rows.Count > MaxRows) Rows.RemoveAt(0);
        OnChanged(nameof(CountText));
        _ = EnrichGeoAsync(row);
    }

    private async Task EnrichGeoAsync(RequestRow row)
    {
        try
        {
            var geo = await _geo.LookupAsync(row.Host).ConfigureAwait(true);
            if (geo is null) return;
            row.ApplyGeo(geo);
            if (ReferenceEquals(row, _selected)) OnChanged(nameof(OverviewText));

            if (geo.CountryCode.Length == 2)
            {
                var flag = await _flags.GetAsync(geo.CountryCode).ConfigureAwait(true);
                if (flag is not null) row.SetFlag(flag);
            }
        }
        catch { /* geo is best-effort */ }
    }

    public string CountText => $"{Rows.Count} request{(Rows.Count == 1 ? "" : "s")}";

    private void Clear()
    {
        Rows.Clear();
        _store.Clear();
        SelectedRow = null;
        OnChanged(nameof(CountText));
    }

    private void SendToTalon()
    {
        if (_selected is null) return;
        Talon.LoadFrom(_selected.Tx);
        SelectedTab = 1; // switch to the Talon tab
    }

    // ---- Filtering -------------------------------------------------------------------------

    private string _filter = "";
    public string FilterText
    {
        get => _filter;
        set { _filter = value ?? ""; OnChanged(); RowsView.Refresh(); }
    }

    private bool FilterRow(object o)
    {
        if (o is not RequestRow r) return false;
        if (!StatusClassMatches(r)) return false;
        if (string.IsNullOrWhiteSpace(_filter)) return true;
        var f = _filter;
        return r.Host.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.PathText.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.Method.Contains(f, StringComparison.OrdinalIgnoreCase)
            || r.StatusText.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    private bool StatusClassMatches(RequestRow r)
    {
        int s = r.Tx.StatusCode;
        return _statusFilter switch
        {
            "2xx" => s is >= 200 and < 300,
            "3xx" => s is >= 300 and < 400,
            "4xx" => s is >= 400 and < 500,
            "5xx" => s is >= 500 and < 600,
            "ERR" => s == 0 || r.Tx.Error is not null,
            _ => true,
        };
    }

    private void SaveResponseBody()
    {
        if (_selected is null || _selected.Tx.ResponseBody.Length == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "response",
            Filter = "All files|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            try { File.WriteAllBytes(dlg.FileName, _selected.Tx.ResponseBody); } catch { }
        }
    }

    // ---- Detail pane -----------------------------------------------------------------------

    private RequestRow? _selected;
    public RequestRow? SelectedRow
    {
        get => _selected;
        set
        {
            _selected = value;
            OnChanged();
            OnChanged(nameof(HasSelection));
            OnChanged(nameof(OverviewText));
            OnChanged(nameof(RequestHeadersText));
            OnChanged(nameof(RequestBodyText));
            OnChanged(nameof(ResponseHeadersText));
            OnChanged(nameof(ResponseBodyText));
        }
    }

    public bool HasSelection => _selected is not null;

    public string OverviewText
    {
        get
        {
            if (_selected is null) return "";
            var t = _selected.Tx;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{t.Method}  {t.Url}");
            sb.AppendLine();
            sb.AppendLine($"App          {(string.IsNullOrEmpty(t.App) ? "—" : $"{t.App}{(t.Pid > 0 ? $"  (pid {t.Pid})" : "")}")}");
            sb.AppendLine($"Status       {(t.StatusCode == 0 ? "—" : t.StatusCode + " " + t.StatusText)}");
            sb.AppendLine($"Protocol     {t.HttpVersion}   ({(t.IsTls ? "TLS / decrypted" : "plaintext")})");
            sb.AppendLine($"Host         {t.Host}:{t.Port}");
            string loc = t.RemoteIp is null
                ? "resolving…"
                : $"{t.RemoteIp}{(string.IsNullOrEmpty(t.CountryName) ? "" : $"  ·  {t.CountryName}")}";
            sb.AppendLine($"Server       {loc}");
            sb.AppendLine($"Client       {t.ClientEndpoint}");
            if (t.RequestContentType is { } rct) sb.AppendLine($"Req type     {rct}");
            if (t.ResponseContentType is { } sct) sb.AppendLine($"Resp type    {sct}");
            string? enc = Transaction.HeaderValue(t.ResponseHeaders, "Content-Encoding");
            if (!string.IsNullOrEmpty(enc)) sb.AppendLine($"Encoding     {enc}");
            string? srv = Transaction.HeaderValue(t.ResponseHeaders, "Server");
            if (!string.IsNullOrEmpty(srv)) sb.AppendLine($"Server hdr   {srv}");
            sb.AppendLine($"Started      {t.Started:HH:mm:ss.fff}");
            string ttfb = t.TtfbMs is { } f ? $"   (TTFB {(int)f} ms)" : "";
            sb.AppendLine($"Duration     {(int)t.Duration.TotalMilliseconds} ms{ttfb}");
            sb.AppendLine($"Req size     {BodyView.Human(t.RequestSize)}  ({t.RequestHeaders.Count} headers)");
            sb.AppendLine($"Resp size    {BodyView.Human(t.ResponseSize)}  ({t.ResponseHeaders.Count} headers)");
            if (t.Error is not null) sb.AppendLine($"Error        {t.Error}");
            return sb.ToString().TrimEnd();
        }
    }

    public string RequestHeadersText => _selected is null ? "" : BodyView.HeadersText(_selected.Tx.RequestHeaders);
    public string RequestBodyText => _selected is null ? "" : BodyView.Format(_selected.Tx.RequestBody, _selected.Tx.RequestContentType);
    public string ResponseHeadersText => _selected is null ? "" : BodyView.HeadersText(_selected.Tx.ResponseHeaders);
    public string ResponseBodyText => _selected is null ? "" : BodyView.Format(_selected.Tx.ResponseBody, _selected.Tx.ResponseContentType);

    // ---- INotifyPropertyChanged ------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
