namespace Cvolo.LanguageServer.Core.Documents;

/// <summary>
/// Identifies a text document by its absolute local filesystem path.
/// Only absolute file: documents are reachable here; a value is produced once
/// at the protocol boundary after the incoming URI has been validated.
/// </summary>
internal readonly record struct DocumentUri
{
    public string LocalPath { get; }
    /// <summary>
    /// True when the value was produced by <see cref="TryCreate"/> or <see cref="Create"/>.
    /// </summary>
    public bool IsValid => LocalPath is not null;

    private DocumentUri(string localPath)
    {
        LocalPath = localPath;
    }


    public static bool TryCreate(string? localPath, out DocumentUri documentUri)
    {
        if (localPath is not null && Path.IsPathFullyQualified(localPath))
        {
            documentUri = new DocumentUri(localPath);
            return true;
        }

        documentUri = default;
        return false;
    }

    public static DocumentUri Create(string localPath)
    {
        if (!TryCreate(localPath, out DocumentUri documentUri))
        {
            throw new ArgumentException($"'{localPath}' is not an absolute local filesystem path.", nameof(localPath));
        }

        return documentUri;
    }

    public override string ToString()
    {
        return IsValid ? LocalPath : "<invalid>";
    }
}