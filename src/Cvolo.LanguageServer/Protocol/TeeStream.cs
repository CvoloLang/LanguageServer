using System.Text;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Stream wrapper that mirrors bytes in and out to a sink. Used to capture raw
/// JSON-RPC payloads when <c>--verbose</c> is active. Logging failures are
/// swallowed: the transport must never break because of diagnostics.
/// </summary>
internal sealed class TeeStream(Stream inner, TextWriter sink) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => inner.CanSeek;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override void Flush() => inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count)
    {
        Log(buffer, offset, count, "=>");
        inner.Write(buffer, offset, count);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
        {
            Log(buffer, offset, read, "<=");
        }

        return read;
    }

    private void Log(byte[] buffer, int offset, int count, string direction)
    {
        try
        {
            sink.WriteLine($"[RAW {direction}] {Encoding.UTF8.GetString(buffer, offset, count)}");
            sink.Flush();
        }
        catch
        {
            // Diagnostics must never disrupt the protocol stream.
        }
    }
}