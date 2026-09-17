using Cvolo.LanguageServer.Core.Logging;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>Captures every Core log message for assertions.</summary>
internal sealed class RecordingCoreLogger : ICoreLogger
{
    public List<(CoreLogLevel Level, string Message)> Messages { get; } = [];

    public void Write(CoreLogLevel level, string message)
    {
        Messages.Add((level, message));
    }

    public bool Has(CoreLogLevel level, string substring)
    {
        return Messages.Any(m => m.Level == level && m.Message.Contains(substring, StringComparison.Ordinal));
    }
}