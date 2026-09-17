namespace Cvolo.LanguageServer.Cvolo;

internal enum ProjectDiscoveryStatus
{
    Found,
    NoProject,
    AmbiguousProject,
}

internal readonly record struct ProjectDiscoveryResult(ProjectDiscoveryStatus Status, string? ProjectDirectory);