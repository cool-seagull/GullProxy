using System.Security.Cryptography.X509Certificates;

namespace GullProxy.SystemIntegration;

/// <summary>
/// Installs/removes the proxy's root CA in the Windows trust store. Prefers the machine store
/// (needs admin) and falls back to the current-user store, so HTTPS interception works even
/// without elevation for that user's own apps.
/// </summary>
public static class CertInstaller
{
    public enum Scope { LocalMachine, CurrentUser, None }

    /// <summary>Adds the CA to a Root store if not already present. Returns the scope used.</summary>
    public static Scope EnsureTrusted(X509Certificate2 ca)
    {
        if (TryAdd(StoreLocation.LocalMachine, ca)) return Scope.LocalMachine;
        if (TryAdd(StoreLocation.CurrentUser, ca)) return Scope.CurrentUser;
        return Scope.None;
    }

    public static void Remove(X509Certificate2 ca)
    {
        TryRemove(StoreLocation.LocalMachine, ca);
        TryRemove(StoreLocation.CurrentUser, ca);
    }

    public static bool IsTrusted(X509Certificate2 ca) =>
        Contains(StoreLocation.LocalMachine, ca) || Contains(StoreLocation.CurrentUser, ca);

    private static bool TryAdd(StoreLocation location, X509Certificate2 ca)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadWrite);
            if (!store.Certificates.Contains(ca))
                store.Add(ca);
            return true;
        }
        catch
        {
            return false; // no permission for this store
        }
    }

    private static void TryRemove(StoreLocation location, X509Certificate2 ca)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadWrite);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false);
            if (found.Count > 0)
                store.RemoveRange(found);
        }
        catch { /* not present or no permission */ }
    }

    private static bool Contains(StoreLocation location, X509Certificate2 ca)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Find(X509FindType.FindByThumbprint, ca.Thumbprint, false).Count > 0;
        }
        catch { return false; }
    }
}
