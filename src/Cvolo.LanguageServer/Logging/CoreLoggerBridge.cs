using Cvolo.LanguageServer.Core.Logging;

namespace Cvolo.LanguageServer.Logging;

/// <summary>Routes core logging through the server's <see cref="ILspLogger"/>.</summary>
internal sealed class CoreLoggerBridge(ILspLogger inner) : ICoreLogger
{
    public void Write(CoreLogLevel level, string message)
    {
        switch (level)
        {
            case CoreLogLevel.Debug:
                inner.Debug(message);
                break;
            case CoreLogLevel.Info:
                inner.Info(message);
                break;
            case CoreLogLevel.Warning:
                inner.Warning(message);
                break;
            case CoreLogLevel.Error:
                inner.Error(message);
                break;
        }
    }
}