using System.Text;

namespace Cvolo.LanguageServer.Logging;

public interface ILspLogger
{
    bool IsVerboseEnabled { get; }
    TextWriter? RawSink { get; }
    void Info(string message);
    void Verbose(string message);
    void Error(string message);
}

/// <summary>
/// Writes diagnostics to stderr and, when configured, to a log file. Never
/// writes to stdout (stdout is reserved for the JSON-RPC protocol). Logging is
/// off by default: informational messages are suppressed unless a log file or
/// verbose mode is configured. Raw protocol payloads are only logged when
/// <c>--verbose</c> is active.
/// </summary>
public sealed class LspLogger(bool verbose, string? logFilePath) : ILspLogger, IDisposable
{
    private readonly TextWriter? _file = logFilePath is null
            ? null
            : new StreamWriter(new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    public bool IsVerboseEnabled { get; } = verbose;

    public TextWriter? RawSink => IsVerboseEnabled ? (_file ?? Console.Error) : null;

    public void Info(string message)
    {
        var line = Format("info", message);
        _file?.WriteLine(line);
        if (IsVerboseEnabled && _file is null)
        {
            Console.Error.WriteLine(line);
        }
    }

    public void Verbose(string message)
    {
        if (!IsVerboseEnabled)
        {
            return;
        }

        var line = Format("verbose", message);
        _file?.WriteLine(line);
        if (_file is null)
        {
            Console.Error.WriteLine(line);
        }
    }

    public void Error(string message)
    {
        _file?.WriteLine(Format("error", message));
        Console.Error.WriteLine(Format("error", message));
    }

    private static string Format(string level, string message)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        return $"{timestamp} [{level}] {message}";
    }

    public void Dispose()
    {
        _file?.Flush();
        _file?.Dispose();
    }
}