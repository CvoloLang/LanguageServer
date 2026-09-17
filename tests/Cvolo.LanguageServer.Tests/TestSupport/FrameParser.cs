using System.Text;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// Splits a raw byte stream into Content-Length delimited JSON-RPC frames.
/// A correct stream consumes every byte; any leftover indicates contamination.
/// </summary>
internal static class FrameParser
{
    public static IReadOnlyList<string> ParseAll(byte[] data) => Parse(data).Frames;

    public static (List<string> Frames, int Consumed) Parse(byte[] data)
    {
        var frames = new List<string>();
        var i = 0;

        while (i < data.Length)
        {
            var headerEnd = FindHeaderTerminator(data, i);
            if (headerEnd < 0)
            {
                break;
            }

            var header = Encoding.ASCII.GetString(data, i, headerEnd - i);
            var contentLength = ReadContentLength(header);
            if (contentLength is null)
            {
                break;
            }

            var bodyStart = headerEnd + 4;
            if (bodyStart + contentLength.Value > data.Length)
            {
                break;
            }

            frames.Add(Encoding.UTF8.GetString(data, bodyStart, contentLength.Value));
            i = bodyStart + contentLength.Value;
        }

        return (frames, i);
    }

    public static byte[] EncodeFrame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        var frame = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, frame, 0, header.Length);
        Buffer.BlockCopy(body, 0, frame, header.Length, body.Length);
        return frame;
    }

    private static int FindHeaderTerminator(byte[] data, int start)
    {
        for (var i = start; i + 3 < data.Length; i++)
        {
            if (data[i] == (byte)'\r' && data[i + 1] == (byte)'\n' &&
                data[i + 2] == (byte)'\r' && data[i + 3] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static int? ReadContentLength(string header)
    {
        foreach (var line in header.Split("\r\n"))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(line.AsSpan(colon + 1).Trim(), out var value))
                {
                    return value;
                }
            }
        }

        return null;
    }
}