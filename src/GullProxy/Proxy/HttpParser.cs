using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace GullProxy.Proxy;

/// <summary>A parsed HTTP request head (start line + headers), plus the body reader state.</summary>
public sealed class RequestHead
{
    public string Method = "";
    public string Target = "";     // origin-form (/path) or authority-form (host:port for CONNECT)
    public string Version = "HTTP/1.1";
    public List<KeyValuePair<string, string>> Headers = new();

    public string? Header(string name)
    {
        foreach (var h in Headers)
            if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                return h.Value;
        return null;
    }
}

/// <summary>A parsed HTTP response head (status line + headers).</summary>
public sealed class ResponseHead
{
    public string Version = "HTTP/1.1";
    public int StatusCode;
    public string ReasonPhrase = "";
    public List<KeyValuePair<string, string>> Headers = new();

    public string? Header(string name)
    {
        foreach (var h in Headers)
            if (string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase))
                return h.Value;
        return null;
    }
}

/// <summary>
/// Minimal, allocation-conscious HTTP/1.1 reader/writer working over raw streams. Handles
/// Content-Length and chunked transfer bodies. Buffers a small amount past the header so a
/// pipelined body start isn't lost.
/// </summary>
public static class HttpParser
{
    private const int MaxHeaderBytes = 256 * 1024;

    /// <summary>
    /// Reads a CRLF-delimited header block from <paramref name="stream"/>. Any bytes read past
    /// the terminating blank line are returned in <paramref name="leftover"/> (body prefix).
    /// Returns null on a clean EOF before any data.
    /// </summary>
    public static async Task<(byte[] head, byte[] leftover)?> ReadHeadAsync(
        Stream stream, CancellationToken ct)
    {
        var buf = new List<byte>(4096);
        var one = new byte[1];
        int matched = 0; // position in CRLFCRLF terminator

        while (true)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0)
                return buf.Count == 0 ? null : (buf.ToArray(), Array.Empty<byte>());

            byte b = one[0];
            buf.Add(b);

            // Track the \r\n\r\n terminator.
            matched = (matched, b) switch
            {
                (0, (byte)'\r') => 1,
                (1, (byte)'\n') => 2,
                (2, (byte)'\r') => 3,
                (3, (byte)'\n') => 4,
                _ => b == (byte)'\r' ? 1 : 0,
            };

            if (matched == 4)
                return (buf.ToArray(), Array.Empty<byte>());

            if (buf.Count > MaxHeaderBytes)
                throw new InvalidDataException("HTTP header block too large.");
        }
    }

    public static RequestHead ParseRequestHead(byte[] head)
    {
        var (start, headers) = SplitHead(head);
        var parts = start.Split(' ', 3);
        if (parts.Length < 3)
            throw new InvalidDataException($"Malformed request line: {start}");
        return new RequestHead { Method = parts[0], Target = parts[1], Version = parts[2], Headers = headers };
    }

    public static ResponseHead ParseResponseHead(byte[] head)
    {
        var (start, headers) = SplitHead(head);
        var parts = start.Split(' ', 3);
        if (parts.Length < 2)
            throw new InvalidDataException($"Malformed status line: {start}");
        _ = int.TryParse(parts[1], out int code);
        return new ResponseHead
        {
            Version = parts[0],
            StatusCode = code,
            ReasonPhrase = parts.Length == 3 ? parts[2] : "",
            Headers = headers,
        };
    }

    private static (string startLine, List<KeyValuePair<string, string>> headers) SplitHead(byte[] head)
    {
        string text = Encoding.Latin1.GetString(head);
        var lines = text.Split("\r\n");
        string start = lines[0];
        var headers = new List<KeyValuePair<string, string>>();
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0) continue; // trailing blank
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            headers.Add(new(name, value));
        }
        return (start, headers);
    }

    /// <summary>
    /// Reads a message body according to Content-Length / Transfer-Encoding: chunked headers.
    /// <paramref name="prefix"/> is any already-read body bytes. Returns the fully assembled
    /// body. When neither framing header is present and <paramref name="bodyAllowed"/> is true,
    /// reads until EOF (HTTP/1.0-style response).
    /// </summary>
    public static async Task<byte[]> ReadBodyAsync(
        Stream stream,
        byte[] prefix,
        string? contentLength,
        string? transferEncoding,
        bool bodyAllowed,
        CancellationToken ct)
    {
        if (transferEncoding != null &&
            transferEncoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            return await ReadChunkedAsync(stream, prefix, ct).ConfigureAwait(false);
        }

        if (long.TryParse(contentLength, out long len))
        {
            var body = new byte[Math.Min(len, int.MaxValue)];
            int have = Math.Min(prefix.Length, body.Length);
            Array.Copy(prefix, body, have);
            while (have < len)
            {
                int n = await stream.ReadAsync(body.AsMemory(have, (int)(len - have)), ct)
                    .ConfigureAwait(false);
                if (n == 0) break;
                have += n;
            }
            return body;
        }

        if (!bodyAllowed)
            return prefix; // no framing => no body for requests

        // Read to EOF (connection-close framing).
        using var ms = new MemoryStream();
        ms.Write(prefix);
        var tmp = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                int n = await stream.ReadAsync(tmp, ct).ConfigureAwait(false);
                if (n == 0) break;
                ms.Write(tmp, 0, n);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(tmp); }
        return ms.ToArray();
    }

    private static async Task<byte[]> ReadChunkedAsync(Stream stream, byte[] prefix, CancellationToken ct)
    {
        var pending = new Queue<byte>(prefix);
        using var body = new MemoryStream();

        async Task<byte> NextByteAsync()
        {
            if (pending.Count > 0) return pending.Dequeue();
            var one = new byte[1];
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("Unexpected EOF in chunked body.");
            return one[0];
        }

        async Task<string> ReadLineAsync()
        {
            var sb = new StringBuilder();
            while (true)
            {
                byte b = await NextByteAsync().ConfigureAwait(false);
                if (b == (byte)'\r')
                {
                    byte lf = await NextByteAsync().ConfigureAwait(false);
                    if (lf == (byte)'\n') break;
                    sb.Append((char)b).Append((char)lf);
                    continue;
                }
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        while (true)
        {
            string sizeLine = await ReadLineAsync().ConfigureAwait(false);
            int semi = sizeLine.IndexOf(';');
            string hex = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();
            if (hex.Length == 0) break;
            int size = Convert.ToInt32(hex, 16);
            if (size == 0)
            {
                // consume trailing headers up to the final blank line
                while ((await ReadLineAsync().ConfigureAwait(false)).Length > 0) { }
                break;
            }
            for (int i = 0; i < size; i++)
                body.WriteByte(await NextByteAsync().ConfigureAwait(false));
            _ = await ReadLineAsync().ConfigureAwait(false); // trailing CRLF after chunk data
        }

        return body.ToArray();
    }

    public static byte[] SerializeRequestHead(RequestHead head)
    {
        var sb = new StringBuilder();
        sb.Append(head.Method).Append(' ').Append(head.Target).Append(' ').Append(head.Version).Append("\r\n");
        foreach (var h in head.Headers)
            sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        sb.Append("\r\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
