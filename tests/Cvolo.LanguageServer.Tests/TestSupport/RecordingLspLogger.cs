using Cvolo.LanguageServer.Logging;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// Captures every log message for assertions.
/// </summary>
internal sealed class RecordingLspLogger : ILspLogger
{
    public List<(string Level, string Message)> Messages { get; } = [];
    public bool IsVerboseEnabled => true;
    public TextWriter? RawSink => null;

    public void Info(string message)
    {
        Messages.Add((nameof(Info), message));
    }

    public void Debug(string message)
    {
        Messages.Add((nameof(Debug), message));
    }

    public void Verbose(string message)
    {
        Messages.Add((nameof(Verbose), message));
    }

    public void Warning(string message)
    {
        Messages.Add((nameof(Warning), message));
    }

    public void Error(string message)
    {
        Messages.Add((nameof(Error), message));
    }

    public bool Has(string level, string substring)
    {
        return Messages.Any(m => m.Level == level && m.Message.Contains(substring, StringComparison.Ordinal));
    }
}