namespace Cvolo.LanguageServer.Core.Logging;

internal interface ICoreLogger
{
    void Write(CoreLogLevel level, string message);
}