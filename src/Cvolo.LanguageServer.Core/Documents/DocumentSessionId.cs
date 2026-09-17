namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// Opaque identity of one open lifetime of a document. A new value is minted
/// on open, and is invalidated (never reused) when the document closes.
/// Not serialized and never exposed over the wire.
/// </summary>
internal readonly record struct DocumentSessionId(long Value)
{
    private static long _next;

    public static DocumentSessionId Next()
    {
        var next = Interlocked.Increment(ref _next);
        return new DocumentSessionId(next);
    }

    public override string ToString()
    {
        return Value.ToString();
    }
}