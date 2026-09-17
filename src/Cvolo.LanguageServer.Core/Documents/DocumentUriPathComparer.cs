namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// Compares <see cref="DocumentUri"/> values by their local path using the
/// platform's path comparison rules (case-insensitive on Windows). Used when
/// correlating compiler-reported paths with client-reported document URIs.
/// </summary>
internal sealed class DocumentUriPathComparer : IEqualityComparer<DocumentUri>
{
    public static DocumentUriPathComparer Instance { get; } = new();

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private DocumentUriPathComparer()
    {
    }

    public bool Equals(DocumentUri x, DocumentUri y)
    {
        return PathComparer.Equals(x.LocalPath, y.LocalPath);
    }

    public int GetHashCode(DocumentUri obj)
    {
        return PathComparer.GetHashCode(obj.LocalPath ?? string.Empty);
    }
}
