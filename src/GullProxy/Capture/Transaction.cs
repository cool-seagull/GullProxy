using System.Collections.Generic;

namespace GullProxy.Capture;

/// <summary>
/// One captured request/response exchange. Bodies are stored decoded (decompressed) and
/// capped to <see cref="TransactionStore.MaxBodyBytes"/> for display; the *Size fields keep
/// the true on-the-wire byte counts.
/// </summary>
public sealed class Transaction
{
    public long Id { get; init; }
    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;

    public string ClientEndpoint { get; set; } = "";
    public string? App { get; set; }   // the local application that sent the request
    public int Pid { get; set; }

    // Request
    public string Method { get; set; } = "";
    public string Scheme { get; set; } = "http";
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string Path { get; set; } = "";
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public List<KeyValuePair<string, string>> RequestHeaders { get; } = new();
    public byte[] RequestBody { get; set; } = Array.Empty<byte>();
    public bool RequestBodyTruncated { get; set; }
    public long RequestSize { get; set; }

    // Response
    public int StatusCode { get; set; }
    public string StatusText { get; set; } = "";
    public List<KeyValuePair<string, string>> ResponseHeaders { get; } = new();
    public byte[] ResponseBody { get; set; } = Array.Empty<byte>();
    public bool ResponseBodyTruncated { get; set; }
    public long ResponseSize { get; set; }

    // Meta
    public bool IsTls { get; set; }
    public bool IsWebSocket { get; set; }
    public TimeSpan Duration { get; set; }
    public double? TtfbMs { get; set; }   // time to first byte (response headers)
    public string? Error { get; set; }

    // Server geolocation (filled in asynchronously after capture)
    public string? RemoteIp { get; set; }
    public string? CountryCode { get; set; }
    public string? CountryName { get; set; }
    public string? Flag { get; set; }

    public string Url => $"{Scheme}://{Host}{(IsDefaultPort ? "" : ":" + Port)}{Path}";

    private bool IsDefaultPort =>
        (Scheme == "http" && Port == 80) || (Scheme == "https" && Port == 443) || Port == 0;

    public string? RequestContentType => HeaderValue(RequestHeaders, "Content-Type");
    public string? ResponseContentType => HeaderValue(ResponseHeaders, "Content-Type");

    public static string? HeaderValue(List<KeyValuePair<string, string>> headers, string name)
    {
        foreach (var h in headers)
            if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                return h.Value;
        return null;
    }
}
