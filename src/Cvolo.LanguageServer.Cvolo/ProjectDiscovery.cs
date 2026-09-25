namespace Cvolo.LanguageServer.Cvolo;

/// <summary>
/// Locates the semantic root that owns a document. The nearest directory walking upward that
/// contains exactly one *.cvlproj wins. When no project exists before the workspace boundary,
/// the document is assigned to a loose workspace rooted at that boundary (or its own directory
/// when discovery is unbounded).
/// </summary>
internal static class ProjectDiscovery
{
    public static ProjectDiscoveryResult FindProject(string documentPath, string? workspaceRoot)
    {
        var directory = Path.GetDirectoryName(documentPath);
        if (string.IsNullOrEmpty(directory))
        {
            return new ProjectDiscoveryResult(ProjectDiscoveryStatus.NoProject, null);
        }

        while (directory is not null)
        {
            var projectFiles = Directory.GetFiles(directory, "*.cvlproj");
            if (projectFiles.Length == 1)
            {
                return new ProjectDiscoveryResult(ProjectDiscoveryStatus.Found, directory);
            }

            if (projectFiles.Length > 1)
            {
                return new ProjectDiscoveryResult(ProjectDiscoveryStatus.AmbiguousProject, directory);
            }

            if (IsSameDirectory(directory, workspaceRoot))
            {
                var looseRoot = ContainsNestedProject(directory)
                    ? Path.GetDirectoryName(documentPath) ?? directory
                    : directory;
                return new ProjectDiscoveryResult(ProjectDiscoveryStatus.LooseWorkspace, looseRoot);
            }

            directory = Path.GetDirectoryName(directory);
        }

        return new ProjectDiscoveryResult(ProjectDiscoveryStatus.LooseWorkspace, Path.GetDirectoryName(documentPath));
    }

    private static bool ContainsNestedProject(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.cvlproj", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Do not broaden a loose workspace across a tree we cannot inspect safely.
            return true;
        }
    }

    private static bool IsSameDirectory(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), comparison);
    }
}