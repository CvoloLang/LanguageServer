namespace Cvolo.LanguageServer.Cvolo;

/// <summary>
/// Locates the project that owns a document: the nearest directory walking
/// upward from the document that contains exactly one *.cvlproj file. The
/// walk never crosses the workspace boundary (most specific containing
/// workspace folder, else rootUri, else no boundary).
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
                return new ProjectDiscoveryResult(ProjectDiscoveryStatus.NoProject, directory);
            }

            directory = Path.GetDirectoryName(directory);
        }

        return new ProjectDiscoveryResult(ProjectDiscoveryStatus.NoProject, null);
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