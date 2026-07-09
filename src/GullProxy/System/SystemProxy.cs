using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace GullProxy.SystemIntegration;

/// <summary>
/// Toggles the Windows (WinINET) system proxy for the current user and — critically — makes the
/// change reversible even if the app crashes or is force-killed. Before enabling the proxy it
/// writes the user's *previous* settings to a backup file on disk; restore reads that file, so a
/// brand-new process (or the next launch) can always put things back. If the true previous
/// settings can't be restored, it falls back to simply disabling the proxy — the safe state.
/// </summary>
public sealed class SystemProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    private static string BackupPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GullProxy", "proxy-backup.json");

    private sealed record Backup(int? Enable, string? Server, string? Override);

    // Never route localhost or the OS/browser connectivity probes through us — otherwise Windows
    // reports "No Internet" and captive-portal checks misfire (that's what made Wi-Fi look dead).
    private const string BypassList =
        "<local>;localhost;127.*;" +
        "*.msftconnecttest.com;msftconnecttest.com;*.msftncsi.com;msftncsi.com;" +
        "detectportal.firefox.com;connectivitycheck.gstatic.com;connectivity-check.ubuntu.com";

    /// <summary>Points the system proxy at 127.0.0.1:<paramref name="port"/>, backing up prior settings.</summary>
    public void Apply(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key is null) return;

        // Only capture the backup if one doesn't already exist, so re-applying (or recovering)
        // never overwrites the genuine original with our own proxy values.
        if (!File.Exists(BackupPath))
        {
            var backup = new Backup(
                key.GetValue("ProxyEnable") as int?,
                key.GetValue("ProxyServer") as string,
                key.GetValue("ProxyOverride") as string);
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
        }

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"127.0.0.1:{port}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", BypassList, RegistryValueKind.String);
        Notify();
    }

    /// <summary>Restores the pre-proxy settings from the on-disk backup. Safe to call repeatedly.</summary>
    public static void Restore()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null) { DisableNow(); return; }

            Backup? backup = null;
            if (File.Exists(BackupPath))
            {
                try { backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath)); } catch { }
            }

            if (backup is null)
            {
                // No backup to trust — at least leave the machine in the safe (proxy-off) state.
                DisableNow();
                return;
            }

            SetOrDelete(key, "ProxyEnable", backup.Enable, RegistryValueKind.DWord);
            SetOrDelete(key, "ProxyServer", backup.Server, RegistryValueKind.String);
            SetOrDelete(key, "ProxyOverride", backup.Override, RegistryValueKind.String);
            TryDeleteBackup();
            Notify();
        }
        catch
        {
            DisableNow();
        }
    }

    /// <summary>Last-resort: turn the system proxy off. Better than leaving it pointed at a dead port.</summary>
    public static void DisableNow()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            TryDeleteBackup();
            Notify();
        }
        catch { /* nothing more we can do */ }
    }

    /// <summary>True if a previous run left a backup behind (i.e. it may have crashed with the proxy on).</summary>
    public static bool HasLeftoverBackup() => File.Exists(BackupPath);

    private static void SetOrDelete(RegistryKey key, string name, object? value, RegistryValueKind kind)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, kind);
    }

    private static void TryDeleteBackup()
    {
        try { if (File.Exists(BackupPath)) File.Delete(BackupPath); } catch { }
    }

    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
    }
}
