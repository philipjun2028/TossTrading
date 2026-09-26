using System.IO.Compression;
using System.Net;
using System.Text;

namespace TossTrading.Toss;

/// <summary>HTTP 공통: 압축 해제 가능한 HttpClient 생성, 응답 본문을 안전하게 텍스트로 읽기</summary>
public static class TossHttp
{
    /// <summary>gzip/deflate/brotli 응답을 자동으로 풀어주는 HttpClient</summary>
    public static HttpClient CreateClient(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = timeout,
    };

    /// <summary>
    /// 응답 본문을 문자열로 읽는다. 자동 해제가 안 된 압축 본문(gzip 매직 바이트, Content-Encoding)은 직접 풀고,
    /// 그래도 텍스트가 아니면 깨진 글자 대신 "해석할 수 없는 본문" 안내를 돌려준다.
    /// </summary>
    public static async Task<string> ReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (bytes.Length == 0) return "";

        var encoding = resp.Content.Headers.ContentEncoding.FirstOrDefault()?.ToLowerInvariant();
        try
        {
            if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B) bytes = Decompress(bytes, s => new GZipStream(s, CompressionMode.Decompress));
            else if (encoding == "br") bytes = Decompress(bytes, s => new BrotliStream(s, CompressionMode.Decompress));
            else if (encoding == "deflate") bytes = Decompress(bytes, s => new ZLibStream(s, CompressionMode.Decompress));
        }
        catch (InvalidDataException)
        {
            // 압축이 아니었으면 원본 그대로
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (LooksBinary(text))
            return $"(해석할 수 없는 응답 본문 {bytes.Length}바이트, Content-Type={resp.Content.Headers.ContentType?.MediaType ?? "-"}, Content-Encoding={encoding ?? "-"})";
        return text;
    }

    private static byte[] Decompress(byte[] data, Func<Stream, Stream> factory)
    {
        using var input = new MemoryStream(data);
        using var z = factory(input);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static bool LooksBinary(string text)
    {
        var sample = text.Length > 400 ? text[..400] : text;
        var bad = sample.Count(c => c == '�' || (char.IsControl(c) && c is not ('\r' or '\n' or '\t')));
        return bad > Math.Max(3, sample.Length / 20);
    }
}
