namespace Cvolo.LanguageServer.Core.Logging;

internal sealed class NullCoreLogger : ICoreLogger
{
    public void Write(CoreLogLevel level, string message)
    {
    }
}