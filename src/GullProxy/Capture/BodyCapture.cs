using System.IO.Compression;

namespace GullProxy.Capture;

/// <summary>
/// Records request/response bodies into a <see cref="Transaction"/>, decoding
/// Content-Encoding (gzip/deflate/br) so the stored body is human-readable. Bodies are already
/// capped by the engine's tee; the true wire size is tracked separately.
/// </summary>
public static class BodyCapture
{
    public static void RecordRequestBody(Transaction tx, byte[] rawBody, long wireSize, bool truncated, string? contentEncoding)
    {
        tx.RequestSize = wireSize;
        tx.RequestBodyTruncated = truncated;
        tx.RequestBody = Decode(rawBody, contentEncoding);
    }

    public static void RecordResponseBody(Transaction tx, byte[] rawBody, long wireSize, bool truncated, string? contentEncoding)
    {
        tx.ResponseSize = wireSize;
        tx.ResponseBodyTruncated = truncated;
        tx.ResponseBody = Decode(rawBody, contentEncoding);
    }

    private static byte[] Decode(byte[] body, string? contentEncoding)
    {
        if (body.Length == 0 || string.IsNullOrEmpty(contentEncoding)) return body;
        try
        {
            using var input = new MemoryStream(body);
            Stream? decompressor = contentEncoding.Trim().ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
                "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                "br" => new BrotliStream(input, CompressionMode.Decompress),
                _ => null,
            };
            if (decompressor is null) return body;
            using (decompressor)
            using (var output = new MemoryStream())
            {
                decompressor.CopyTo(output);
                return output.ToArray();
            }
        }
        catch
        {
            return body; // partial/truncated compressed data — show raw
        }
    }
}
