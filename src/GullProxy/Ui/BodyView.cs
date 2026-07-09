using System.Text;
using System.Text.Json;

namespace GullProxy.Ui;

/// <summary>Turns a captured body into readable text for the detail pane.</summary>
public static class BodyView
{
    public static string Format(byte[] body, string? contentType)
    {
        if (body.Length == 0) return "";
        contentType ??= "";

        string text;
        try { text = Encoding.UTF8.GetString(body); }
        catch { return $"<{body.Length:N0} bytes binary>"; }

        // Reject obviously-binary content (many control chars) rather than dumping gibberish.
        int control = 0;
        foreach (char c in text)
            if (char.IsControl(c) && c is not '\n' and not '\r' and not '\t') control++;
        if (control > text.Length / 20 + 8)
            return $"<{body.Length:N0} bytes {(contentType.Length > 0 ? contentType : "binary")}>";

        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || LooksLikeJson(text))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                return JsonSerializer.Serialize(doc.RootElement, JsonOptions);
            }
            catch { /* not valid JSON after all */ }
        }
        return text;
    }

    private static bool LooksLikeJson(string s)
    {
        var t = s.AsSpan().Trim();
        return t.Length > 1 && (t[0] == '{' || t[0] == '[');
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string HeadersText(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var sb = new StringBuilder();
        foreach (var h in headers)
            sb.Append(h.Key).Append(": ").Append(h.Value).Append('\n');
        return sb.ToString().TrimEnd('\n');
    }

    public static string Human(long bytes)
    {
        if (bytes <= 0) return "";
        string[] u = { "B", "KB", "MB", "GB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} {u[i]}" : $"{v:0.#} {u[i]}";
    }
}
