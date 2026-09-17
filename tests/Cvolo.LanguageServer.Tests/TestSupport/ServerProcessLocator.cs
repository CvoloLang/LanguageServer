namespace Cvolo.LanguageServer.Tests.TestSupport;

internal static class ServerProcessLocator
{
    public static (string FileName, string? LeadingArg) Locate()
    {
        var baseDir = AppContext.BaseDirectory;

        var appHost = Path.Combine(baseDir, "cvolo-language-server.exe");
        if (File.Exists(appHost))
        {
            return (appHost, null);
        }

        var native = Path.Combine(baseDir, "cvolo-language-server");
        if (File.Exists(native))
        {
            return (native, null);
        }

        var dll = Path.Combine(baseDir, "cvolo-language-server.dll");
        if (File.Exists(dll))
        {
            return ("dotnet", dll);
        }

        throw new FileNotFoundException(
            $"The server executable was not found in {baseDir}. Expected cvolo-language-server[.exe] or cvolo-language-server.dll copied from the server build output.");
    }
}