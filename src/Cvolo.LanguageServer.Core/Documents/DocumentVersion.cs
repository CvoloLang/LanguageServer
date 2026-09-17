namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// Monotonic per-session document version as published by the client.
/// Ordering decisions (stale/duplicate checks) live in <see cref="DocumentStore"/>.
/// </summary>
internal readonly record struct DocumentVersion(int Value)
{
    public override string ToString()
    {
        return Value.ToString();
    }
}