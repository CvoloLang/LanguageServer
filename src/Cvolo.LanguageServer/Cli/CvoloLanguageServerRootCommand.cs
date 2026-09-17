using System.CommandLine;

namespace Cvolo.LanguageServer.Cli;

/// <summary>
/// The <c>cvolo-language-server [options]</c> command line surface. Parsing is
/// delegated to System.CommandLine so unknown options fail fast with a
/// non-zero exit code before the JSON-RPC transport is started. The built-in
/// <c>--version</c> option is removed so the server can print its three-line
/// version banner instead of the single-line assembly version.
/// </summary>
internal sealed class CvoloLanguageServerRootCommand : RootCommand
{
    private readonly Option<bool> _stdioOption = new("--stdio")
    {
        Description = "Send and receive JSON-RPC over standard input and output (the default and only transport).",
    };

    private readonly Option<string?> _logOption = new("--log")
    {
        Description = "Write diagnostics to the given file path. Logging is off by default.",
    };

    private readonly Option<bool> _verboseOption = new("--verbose")
    {
        Description = "Enable verbose logging, including raw JSON-RPC payloads.",
    };

    private readonly Option<bool> _versionOption = new("--version")
    {
        Description = "Print version information and exit without starting the server.",
    };

    public CvoloLanguageServerRootCommand() : base("Language Server Protocol (LSP 3.17) server for the Cvolo language.")
    {
        Options.Remove(Options.First(o => o is VersionOption or { Name: "--version" }));

        Add(_stdioOption);
        Add(_logOption);
        Add(_verboseOption);
        Add(_versionOption);

        SetAction(HandleAsync);
    }

    private async Task<int> HandleAsync(ParseResult parseResult)
    {
        try
        {
            if (parseResult.GetValue(_versionOption))
            {
                ServerPipeline.PrintVersion();
                return 0;
            }

            var verbose = parseResult.GetValue(_verboseOption);
            var logPath = parseResult.GetValue(_logOption);
            return await ServerPipeline.RunAsync(logPath, verbose).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ServerMetadata.ServerName}: fatal error: {ex}");
            return 1;
        }
    }
}