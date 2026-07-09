using System.ComponentModel;
using System.Text;
using System.Windows.Media;
using GullProxy.Capture;
using GullProxy.Net;

namespace GullProxy.Ui;

/// <summary>Display wrapper around a captured <see cref="Transaction"/> for the request grid.
/// Geolocation arrives asynchronously, so the geo-derived properties raise change notifications.</summary>
public sealed class RequestRow : INotifyPropertyChanged
{
    public Transaction Tx { get; }

    public RequestRow(Transaction tx) => Tx = tx;

    public long Id => Tx.Id;
    public string Time => Tx.Started.ToString("HH:mm:ss.fff");
    public string Method => Tx.Method;
    public string Scheme => Tx.IsTls ? "https" : Tx.Scheme;
    public bool IsTls => Tx.IsTls;
    public string Host => Tx.Host;
    public string PathText => Tx.Path;
    public string AppText => Tx.App ?? "";

    public string StatusText => Tx.StatusCode == 0
        ? (Tx.Error is not null ? "ERR" : "···")
        : Tx.StatusCode.ToString();

    public Brush StatusBrush => Tx.StatusCode switch
    {
        0 => Tx.Error is not null ? Palette.Red : Palette.Muted,
        >= 500 => Palette.Red,
        >= 400 => Palette.Orange,
        >= 300 => Palette.Yellow,
        >= 200 => Palette.Green,
        >= 100 => Palette.Info,
        _ => Palette.Muted,
    };

    public Brush MethodBrush => Method.ToUpperInvariant() switch
    {
        "GET" => MethodPalette.Get,
        "POST" => MethodPalette.Post,
        "PUT" => MethodPalette.Put,
        "PATCH" => MethodPalette.Patch,
        "DELETE" => MethodPalette.Delete,
        "HEAD" or "OPTIONS" => MethodPalette.Head,
        _ => MethodPalette.Other,
    };

    public string TypeText => Tx.IsWebSocket ? "websocket" : ShortType(Tx.ResponseContentType ?? Tx.RequestContentType);
    public string SizeText => BodyView.Human(Tx.ResponseSize);
    public string DurationText => Tx.Duration.TotalMilliseconds >= 1 ? $"{(int)Tx.Duration.TotalMilliseconds} ms" : "";

    public string Url => Tx.Url;

    // ---- Geolocation -----------------------------------------------------------------------

    public string CountryText => Tx.CountryName == "Local" ? "Local" : (Tx.CountryCode ?? "");

    public string IpText => Tx.RemoteIp ?? "";
    public string CountryTooltip => Tx.CountryName ?? "";

    private ImageSource? _flagImage;
    public ImageSource? FlagImage
    {
        get => _flagImage;
        private set { _flagImage = value; Raise(nameof(FlagImage)); }
    }

    public void ApplyGeo(GeoInfo geo)
    {
        Tx.RemoteIp = geo.Ip;
        Tx.CountryCode = geo.CountryCode;
        Tx.CountryName = geo.CountryName;
        Tx.Flag = geo.Flag;
        Raise(nameof(CountryText));
        Raise(nameof(IpText));
        Raise(nameof(CountryTooltip));
    }

    public void SetFlag(ImageSource? flag) => FlagImage = flag;

    // ---- Export ----------------------------------------------------------------------------

    /// <summary>Builds an equivalent cURL command for this request.</summary>
    public string ToCurl()
    {
        var sb = new StringBuilder();
        sb.Append("curl '").Append(Tx.Url).Append('\'');
        if (!Tx.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            sb.Append(" -X ").Append(Tx.Method);
        foreach (var h in Tx.RequestHeaders)
        {
            if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(" \\\n  -H '").Append(h.Key).Append(": ").Append(h.Value.Replace("'", "'\\''")).Append('\'');
        }
        if (Tx.RequestBody.Length > 0)
        {
            string body = Encoding.UTF8.GetString(Tx.RequestBody).Replace("'", "'\\''");
            sb.Append(" \\\n  --data-raw '").Append(body).Append('\'');
        }
        return sb.ToString();
    }

    /// <summary>Renders this request as portable TalonFormat text.</summary>
    public string ToTalonFormat() => TalonFormat.FromTransaction(Tx);

    /// <summary>Renders this request as TalonScript that rebuilds it on the <c>request</c> object.</summary>
    public string ToTalonScript() => TalonFormat.ToTalonScript(Tx);

    private static string ShortType(string? ct)
    {
        if (string.IsNullOrEmpty(ct)) return "";
        int semi = ct.IndexOf(';');
        string t = (semi >= 0 ? ct[..semi] : ct).Trim();
        int slash = t.IndexOf('/');
        return slash >= 0 ? t[(slash + 1)..] : t;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static class Palette
    {
        public static readonly Brush Red = Frozen(0xF0, 0x61, 0x6B);
        public static readonly Brush Orange = Frozen(0xE2, 0xA0, 0x3F);
        public static readonly Brush Yellow = Frozen(0xD7, 0xC4, 0x4C);
        public static readonly Brush Green = Frozen(0x5B, 0xC8, 0x8A);
        public static readonly Brush Info = Frozen(0x4C, 0x93, 0xF0);
        public static readonly Brush Muted = Frozen(0x8A, 0x90, 0x9C);
    }

    private static class MethodPalette
    {
        public static readonly Brush Get = Frozen(0x4F, 0xB4, 0x77);
        public static readonly Brush Post = Frozen(0x4C, 0x93, 0xF0);
        public static readonly Brush Put = Frozen(0xE0, 0xA6, 0x4B);
        public static readonly Brush Patch = Frozen(0xC8, 0x8A, 0xE0);
        public static readonly Brush Delete = Frozen(0xF0, 0x61, 0x6B);
        public static readonly Brush Head = Frozen(0x6C, 0x9C, 0xA8);
        public static readonly Brush Other = Frozen(0x8A, 0x90, 0x9C);
    }
}
