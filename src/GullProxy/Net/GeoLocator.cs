using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace GullProxy.Net;

/// <summary>Country/IP for a host.</summary>
public sealed record GeoInfo(string Ip, string CountryCode, string CountryName)
{
    /// <summary>Regional-indicator flag emoji for the ISO country code (e.g. "US" → 🇺🇸).</summary>
    public string Flag
    {
        get
        {
            if (CountryCode.Length != 2) return "";
            var cc = CountryCode.ToUpperInvariant();
            if (cc[0] < 'A' || cc[0] > 'Z' || cc[1] < 'A' || cc[1] > 'Z') return "";
            return char.ConvertFromUtf32(0x1F1E6 + (cc[0] - 'A')) + char.ConvertFromUtf32(0x1F1E6 + (cc[1] - 'A'));
        }
    }
}

/// <summary>
/// Resolves the country a host's server sits in. Results are cached per host. DNS resolution is
/// local; the country lookup calls the free ip-api.com service directly (never through our own
/// proxy, so it isn't captured or looped). Private/loopback addresses are reported as "Local"
/// without any external call. All failures degrade to null — geo is best-effort.
/// </summary>
public sealed class GeoLocator
{
    private readonly ConcurrentDictionary<string, Task<GeoInfo?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;

    public bool Enabled { get; set; } = true;

    public GeoLocator()
    {
        // Direct connection — do NOT use the system proxy (that would loop back into us).
        var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(6) };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public Task<GeoInfo?> LookupAsync(string host) =>
        _cache.GetOrAdd(host, h => Task.Run(() => ResolveAsync(h)));

    private async Task<GeoInfo?> ResolveAsync(string host)
    {
        try
        {
            IPAddress? ip = IPAddress.TryParse(host, out var direct)
                ? direct
                : (await Dns.GetHostAddressesAsync(host).ConfigureAwait(false)).FirstOrDefault();
            if (ip is null) return null;

            if (IsLocal(ip))
                return new GeoInfo(ip.ToString(), "", "Local");

            if (!Enabled)
                return new GeoInfo(ip.ToString(), "", "");

            // ip-api.com: 45 requests/min free, no key. HTTP only on the free tier.
            using var resp = await _http.GetAsync(
                $"http://ip-api.com/json/{ip}?fields=status,country,countryCode").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return new GeoInfo(ip.ToString(), "", "");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            var root = doc.RootElement;
            if (root.TryGetProperty("status", out var st) && st.GetString() == "success")
            {
                string cc = root.TryGetProperty("countryCode", out var c) ? c.GetString() ?? "" : "";
                string name = root.TryGetProperty("country", out var n) ? n.GetString() ?? "" : "";
                return new GeoInfo(ip.ToString(), cc, name);
            }
            return new GeoInfo(ip.ToString(), "", "");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsLocal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4)
            return b[0] == 10
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 169 && b[1] == 254);
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
    }
}
