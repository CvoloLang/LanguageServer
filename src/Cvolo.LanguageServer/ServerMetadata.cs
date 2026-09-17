using System.Reflection;

namespace Cvolo.LanguageServer;

internal static class ServerMetadata
{
    public const string ServerName = "cvolo-language-server";

    public static string ServerVersion { get; } =
        typeof(ServerMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static string? ToolingVersion { get; } = GetMetadata("ToolingVersion");

    public static string? CompilerCompatibilityLine { get; } = GetMetadata("CompilerCompatibilityLine");

    private static string? GetMetadata(string key)
    {
        return typeof(ServerMetadata).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;
    }
}