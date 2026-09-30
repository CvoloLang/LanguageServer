using System.Reflection;
using System.Runtime.InteropServices;

namespace Cvolo.LanguageServer;

/// <summary>
/// The identity of this Language Server build. Every value here is embedded at
/// build time, so it describes the artifact that is actually running rather than
/// whatever the release pipeline believed it was packaging.
///
/// Three identities are tracked and they are deliberately independent:
/// <list type="bullet">
///   <item><description><see cref="ServerVersion"/> - the LanguageServer product version.</description></item>
///   <item><description><see cref="ToolingVersion"/> - the exact pinned Compiler.Tooling revision.</description></item>
///   <item><description><see cref="CompilerCompatibilityLine"/> - the compiler line this server supports.</description></item>
/// </list>
/// The compiler line is compatibility metadata; it never forms part of the
/// product version.
/// </summary>
internal static class ServerMetadata
{
    public const string ServerName = "cvolo-language-server";

    public const string ProductName = "Cvolo Language Server";

    /// <summary>Placeholder for a value the build could not determine.</summary>
    public const string Unknown = "unknown";

    public static string ServerVersion { get; } =
        typeof(ServerMetadata).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>
    /// The exact commit this binary was built from. Release builds receive it as an
    /// explicit build property and the release pipeline refuses to package a bundle
    /// whose embedded commit is not a full SHA.
    /// </summary>
    public static string LanguageServerCommit { get; } = GetMetadata("LanguageServerCommit") ?? Unknown;

    public static string? ToolingVersion { get; } = GetMetadata("ToolingVersion");

    /// <summary>The commit of the pinned Compiler.Tooling bundle shipped beside this server.</summary>
    public static string? ToolingCommit { get; } = GetMetadata("ToolingCommit");

    public static string? CompilerCompatibilityLine { get; } = GetMetadata("CompilerCompatibilityLine");

    public static string TargetFramework { get; } = GetMetadata("TargetFramework") ?? Unknown;

    /// <summary>The bundled .NET runtime version, when the build could determine it.</summary>
    public static string? RuntimeVersion { get; } = GetMetadata("RuntimeVersion");

    /// <summary>
    /// The runtime identifier this binary was published for. A RID-agnostic build
    /// has no identifier, so the value the runtime reports is used instead; for a
    /// self-contained single-file publish that is the identifier it was built for.
    /// </summary>
    public static string RuntimeIdentifier { get; } =
        GetMetadata("RuntimeIdentifier") ?? RuntimeInformation.RuntimeIdentifier;

    private static string? GetMetadata(string key)
    {
        var value = typeof(ServerMetadata).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value;

        // A build that cannot determine a value still writes the key with an empty
        // value. Treat that the same as an absent key so the caller can fall back
        // instead of reporting a blank.
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
