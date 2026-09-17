namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>Temporary on-disk workspace: a project directory with a minimal .cvlproj and document files.</summary>
internal sealed class TestWorkspace : IDisposable
{
    private const string ProjectFileName = "App.cvlproj";
    private const string ProjectXml = "<Project><ItemGroup /></Project>\r\n";

    private TestWorkspace(string directoryPath, string projectFilePath)
    {
        DirectoryPath = directoryPath;
        ProjectFilePath = projectFilePath;
    }

    public string DirectoryPath { get; }
    public string ProjectFilePath { get; }

    public string PathOf(string relativePath)
    {
        return Path.Combine(DirectoryPath, relativePath);
    }

    public Uri DocumentUri(string relativePath)
    {
        return new Uri(PathOf(relativePath));
    }

    public static TestWorkspace CreateProject(string[] documentRelativePaths, Func<string, string>? content = null)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "cvolo-ls", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        var projectFilePath = Path.Combine(directoryPath, ProjectFileName);
        File.WriteAllText(projectFilePath, ProjectXml);

        foreach (var relative in documentRelativePaths)
        {
            var full = Path.Combine(directoryPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content?.Invoke(relative) ?? "int Main() { return 0; }");
        }

        return new TestWorkspace(directoryPath, projectFilePath);
    }

    public static TestWorkspace CreateDirectory(params string[] documentRelativePaths)
    {
        var directoryPath = Path.Combine(Path.GetTempPath(), "cvolo-ls", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directoryPath);
        foreach (var relative in documentRelativePaths)
        {
            var full = Path.Combine(directoryPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "int Main() { return 0; }");
        }

        return new TestWorkspace(directoryPath, Path.Combine(directoryPath, ProjectFileName));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch
        {
        }
    }
}