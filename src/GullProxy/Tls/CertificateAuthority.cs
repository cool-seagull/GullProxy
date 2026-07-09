using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GullProxy.Tls;

/// <summary>
/// The proxy's own root certificate authority. Generated once with pure .NET APIs (no OpenSSL)
/// and persisted as a .pfx under %LOCALAPPDATA%\GullProxy. It mints short-lived leaf
/// certificates for each intercepted host, signed by the root, so the client's TLS stack
/// trusts our man-in-the-middle endpoint after the root is installed in the trust store.
/// </summary>
public sealed class CertificateAuthority
{
    private const string SubjectName = "CN=GullProxy Root CA, O=GullProxy";

    private readonly X509Certificate2 _ca;

    private CertificateAuthority(X509Certificate2 ca) => _ca = ca;

    public X509Certificate2 Certificate => _ca;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GullProxy");

    /// <summary>Loads the CA from disk, generating and saving a new one on first run.</summary>
    public static CertificateAuthority LoadOrCreate(string? directory = null)
    {
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        string pfxPath = Path.Combine(directory, "rootCA.pfx");

        if (File.Exists(pfxPath))
        {
            var loaded = X509CertificateLoader.LoadPkcs12(
                File.ReadAllBytes(pfxPath), password: null,
                keyStorageFlags: X509KeyStorageFlags.Exportable);
            return new CertificateAuthority(loaded);
        }

        var ca = CreateRootCa();
        File.WriteAllBytes(pfxPath, ca.Export(X509ContentType.Pkcs12));
        // Also drop a .crt for manual inspection/import if ever needed.
        File.WriteAllText(Path.Combine(directory, "rootCA.crt"),
            new string(PemEncoding.Write("CERTIFICATE", ca.RawData)));
        return new CertificateAuthority(ca);
    }

    private static X509Certificate2 CreateRootCa()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(SubjectName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature,
            critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        var now = DateTimeOffset.UtcNow;
        return req.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }

    /// <summary>Mints a leaf certificate (with private key) valid for <paramref name="host"/>.</summary>
    public X509Certificate2 CreateLeaf(string host)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={host}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") /* serverAuth */ }, critical: false));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        if (System.Net.IPAddress.TryParse(host, out var ip))
            san.AddIpAddress(ip);
        else
            san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        byte[] serial = new byte[16];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F; // ensure positive

        using var leaf = req.Create(_ca, now.AddDays(-1), now.AddYears(1), serial);
        var withKey = leaf.CopyWithPrivateKey(rsa);

        // Re-import as a persistable pfx so SslStream can use the private key on Windows.
        return X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pkcs12), password: null,
            keyStorageFlags: X509KeyStorageFlags.Exportable);
    }
}
