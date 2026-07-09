using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;

namespace GullProxy.Tls;

/// <summary>Caches minted leaf certificates per host so each TLS handshake reuses one.</summary>
public sealed class LeafCertCache
{
    private readonly CertificateAuthority _ca;
    private readonly ConcurrentDictionary<string, Lazy<X509Certificate2>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public LeafCertCache(CertificateAuthority ca) => _ca = ca;

    public X509Certificate2 Get(string host) =>
        _cache.GetOrAdd(host, h => new Lazy<X509Certificate2>(() => _ca.CreateLeaf(h))).Value;
}
