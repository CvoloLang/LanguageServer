namespace Cvolo.LanguageServer.Protocol;

/// <summary>Creates the platform-appropriate client process watcher.</summary>
internal static class ClientProcessWatcherFactory
{
    public static IClientProcessWatcher Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsClientProcessWatcher();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return new PosixClientProcessWatcher();
        }

        return new NoopClientProcessWatcher();
    }
}