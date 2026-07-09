using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GullProxy.Net;

/// <summary>
/// Fetches small country-flag PNGs (Windows can't render flag emoji, so we use real images).
/// Images come from the free flagcdn.com service, are cached per country code, and are frozen so
/// they can be handed straight to the UI thread. Best-effort — failures just yield no flag.
/// </summary>
public sealed class FlagProvider
{
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _http;

    public FlagProvider()
    {
        var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(6) };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public Task<ImageSource?> GetAsync(string countryCode)
    {
        if (countryCode.Length != 2) return Task.FromResult<ImageSource?>(null);
        return _cache.GetOrAdd(countryCode, cc => Task.Run(() => FetchAsync(cc)));
    }

    private async Task<ImageSource?> FetchAsync(string cc)
    {
        try
        {
            byte[] bytes = await _http.GetByteArrayAsync($"https://flagcdn.com/w40/{cc.ToLowerInvariant()}.png")
                .ConfigureAwait(false);
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
            }
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
