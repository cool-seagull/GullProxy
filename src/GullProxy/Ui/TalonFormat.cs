using System.Text;
using System.Text.RegularExpressions;
using GullProxy.Capture;

namespace GullProxy.Ui;

/// <summary>A request parsed out of TalonFormat text (raw — variables not yet substituted).</summary>
public sealed class ParsedTalon
{
    public string Method = "GET";
    public string Url = "";
    public List<KeyValuePair<string, string>> Headers = new();
    public string Body = "";
    public Dictionary<string, string> Vars = new(StringComparer.OrdinalIgnoreCase);
    public string PreScript = "";   // JS from  < {% ... %}  blocks (runs before sending)
    public string PostScript = "";  // JS from  > {% ... %}  blocks (runs after the response)
}

/// <summary>
/// TalonFormat — a single-document format for a whole HTTP request (like a .http / REST Client
/// file), with optional embedded JavaScript:
///
///   # comments start with # or //
///   @name = value            variables, usable anywhere as {{name}}
///
///   METHOD https://url        the request line
///   Header-Name: value        headers, one per line   (a blank line ends them)
///   ...request body...        everything after the blank line
///
///   &lt; {% js %}             pre-request script  — can change request.* and vars.*
///   &gt; {% js %}             post-response script — can read response.* and set vars.*
///
/// Substitution of {{vars}} is deferred to the caller so pre-request scripts can set variables
/// first.
/// </summary>
public static class TalonFormat
{
    private static readonly Regex ScriptBlock =
        new(@"(?sm)^[ \t]*([<>])[ \t]*\{%(.*?)%\}[ \t]*$", RegexOptions.Compiled);

    public static ParsedTalon Parse(string text)
    {
        var parsed = new ParsedTalon();
        text ??= "";

        // 1) pull out < {% %} (pre) and > {% %} (post) script blocks, then strip them
        var pre = new StringBuilder();
        var post = new StringBuilder();
        foreach (Match m in ScriptBlock.Matches(text))
        {
            var target = m.Groups[1].Value == "<" ? pre : post;
            target.Append(m.Groups[2].Value.Trim('\n')).Append('\n');
        }
        parsed.PreScript = pre.ToString().Trim();
        parsed.PostScript = post.ToString().Trim();
        text = ScriptBlock.Replace(text, "");

        // 2) parse the remaining request document
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;
        string? requestLine = null;

        for (; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0 || t.StartsWith('#') || t.StartsWith("//")) continue;
            if (t.StartsWith('@')) { AddVar(parsed.Vars, t); continue; }
            requestLine = t;
            i++;
            break;
        }

        if (requestLine is not null)
        {
            var toks = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length >= 2 && Regex.IsMatch(toks[0], "^[A-Za-z]+$"))
            {
                parsed.Method = toks[0].ToUpperInvariant();
                parsed.Url = toks[1];
            }
            else
            {
                parsed.Method = "GET";
                parsed.Url = toks.Length > 0 ? toks[0] : "";
            }
        }

        for (; i < lines.Length; i++)
        {
            string t = lines[i].Trim();
            if (t.Length == 0) { i++; break; }
            if (t.StartsWith('#') || t.StartsWith("//")) continue;
            if (t.StartsWith('@')) { AddVar(parsed.Vars, t); continue; }
            int colon = t.IndexOf(':');
            if (colon > 0) parsed.Headers.Add(new(t[..colon].Trim(), t[(colon + 1)..].Trim()));
        }

        if (i < lines.Length)
            parsed.Body = string.Join('\n', lines.Skip(i)).Trim('\n');

        return parsed;
    }

    /// <summary>Resolves a relative request line ("GET /path" + Host header) to an absolute URL.</summary>
    public static void ResolveRelativeUrl(ParsedTalon r)
    {
        if (r.Url.Contains("://", StringComparison.Ordinal)) return;
        string? host = r.Headers.FirstOrDefault(h => h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrEmpty(host))
            r.Url = "https://" + host + (r.Url.StartsWith('/') ? r.Url : "/" + r.Url);
    }

    /// <summary>Substitutes {{name}} using the supplied variables, in the URL, header values and body.</summary>
    public static void ApplyVariables(ParsedTalon r, Dictionary<string, string> vars)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            r.Url = Sub(r.Url, vars);
            r.Body = Sub(r.Body, vars);
            for (int h = 0; h < r.Headers.Count; h++)
                r.Headers[h] = new(r.Headers[h].Key, Sub(r.Headers[h].Value, vars));
        }
    }

    public static string Sub(string input, Dictionary<string, string> vars)
    {
        if (vars.Count == 0 || string.IsNullOrEmpty(input)) return input;
        return Regex.Replace(input, @"\{\{\s*([\w.-]+)\s*\}\}", m =>
            vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    /// <summary>Renders a captured transaction as TalonFormat text.</summary>
    public static string FromTransaction(Transaction tx)
    {
        var sb = new StringBuilder();
        sb.Append(tx.Method).Append(' ').Append(tx.Url).Append('\n');
        foreach (var h in tx.RequestHeaders)
        {
            if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(h.Key).Append(": ").Append(h.Value).Append('\n');
        }
        if (tx.RequestBody.Length > 0)
            sb.Append('\n').Append(Encoding.UTF8.GetString(tx.RequestBody));
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Renders a captured request as TalonScript that rebuilds it on the <c>request</c> object.
    /// Paste it inside a <c>&lt; {% … %}</c> pre-request block.</summary>
    public static string ToTalonScript(Transaction tx)
    {
        var sb = new StringBuilder();
        sb.Append("request.method = ").Append(Quote(tx.Method)).Append('\n');
        sb.Append("request.url = ").Append(Quote(tx.Url)).Append('\n');
        foreach (var h in tx.RequestHeaders)
        {
            if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append("request.headers[").Append(Quote(h.Key)).Append("] = ").Append(Quote(h.Value)).Append('\n');
        }
        if (tx.RequestBody.Length > 0)
            sb.Append("request.body = ").Append(Quote(Encoding.UTF8.GetString(tx.RequestBody))).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Quotes a string as a TalonScript string literal.</summary>
    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
            sb.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>Builds an equivalent cURL command from a parsed request.</summary>
    public static string ToCurl(ParsedTalon r)
    {
        var sb = new StringBuilder();
        sb.Append("curl '").Append(r.Url).Append('\'');
        if (!r.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            sb.Append(" -X ").Append(r.Method);
        foreach (var h in r.Headers)
        {
            if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(" \\\n  -H '").Append(h.Key).Append(": ").Append(h.Value.Replace("'", "'\\''")).Append('\'');
        }
        if (r.Body.Length > 0)
            sb.Append(" \\\n  --data-raw '").Append(r.Body.Replace("'", "'\\''")).Append('\'');
        return sb.ToString();
    }

    private static void AddVar(Dictionary<string, string> vars, string line)
    {
        string s = line[1..];
        int eq = s.IndexOf('=');
        if (eq <= 0) return;
        vars[s[..eq].Trim()] = s[(eq + 1)..].Trim();
    }
}
