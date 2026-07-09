namespace GullProxy.Net;

/// <summary>
/// Firefox does not use the Windows certificate store, so installing our root CA into Windows
/// isn't enough — Firefox rejects every intercepted HTTPS site. Setting
/// <c>security.enterprise_roots.enabled = true</c> tells Firefox to honor the Windows root
/// store (including our CA). We write it to each profile's <c>user.js</c> (which Firefox applies
/// on every start). Reversible: delete the line, or the whole user.js, to undo.
/// </summary>
public static class BrowserTrust
{
    private const string Pref = "security.enterprise_roots.enabled";
    private const string PrefLine = "user_pref(\"security.enterprise_roots.enabled\", true);";

    public sealed record Result(int ProfilesFound, int ProfilesChanged);

    public static Result EnableFirefoxTrust()
    {
        int found = 0, changed = 0;
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox", "Profiles");
            if (!Directory.Exists(root)) return new Result(0, 0);

            foreach (var profile in Directory.GetDirectories(root))
            {
                // A real profile has a prefs.js (or times.json); skip stray folders.
                if (!File.Exists(Path.Combine(profile, "prefs.js")) &&
                    !File.Exists(Path.Combine(profile, "times.json")))
                    continue;

                found++;
                string userJs = Path.Combine(profile, "user.js");
                string existing = File.Exists(userJs) ? File.ReadAllText(userJs) : "";
                if (existing.Contains(Pref, StringComparison.OrdinalIgnoreCase))
                    continue; // already configured

                string prefix = existing.Length > 0 && !existing.EndsWith('\n') ? "\n" : "";
                File.AppendAllText(userJs,
                    $"{prefix}// Added by GullProxy so Firefox trusts the local inspection CA.\n{PrefLine}\n");
                changed++;
            }
        }
        catch { /* best effort — Firefox may not be installed */ }
        return new Result(found, changed);
    }
}
