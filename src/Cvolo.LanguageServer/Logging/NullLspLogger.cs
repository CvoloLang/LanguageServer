namespace Cvolo.LanguageServer.Logging;

/// <summary>Logger that performs no work, for tests and bare sessions.</summary>
public sealed class NullLspLogger : ILspLogger
{
    public bool IsVerboseEnabled => false;

    public TextWriter? RawSink => null;

    public void Info(string message)
    {
    }

    public void Verbose(string message)
    {
    }

    public void Error(string message)
    {
    }
}