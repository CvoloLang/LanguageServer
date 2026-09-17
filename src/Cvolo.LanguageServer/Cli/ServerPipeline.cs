using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Cli;

/// <summary>
/// Starts and runs the stdio JSON-RPC session. All diagnostics go to stderr or
/// the log file; stdout carries only JSON-RPC frames while the transport lives.
/// </summary>
internal static class ServerPipeline
{
    public static async Task<int> RunAsync(string? logPath, bool verbose)
    {
        using LspLogger logger = new(verbose, logPath);

        Console.Error.WriteLine($"{ServerMetadata.ServerName} {ServerMetadata.ServerVersion}");
        Console.Error.WriteLine($"tooling {ServerMetadata.ToolingVersion}");
        Console.Error.WriteLine($"compiler-line {ServerMetadata.CompilerCompatibilityLine}");
        logger.Info($"{ServerMetadata.ServerName} {ServerMetadata.ServerVersion} starting");

        TerminationRequest termination = new();
        using IClientProcessWatcher clientWatcher = ClientProcessWatcherFactory.Create();
        using Protocol.LanguageServer server = new(termination, logger, clientWatcher);

        JsonMessageFormatter formatter = new();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
        formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;
        Stream stdout = Console.OpenStandardOutput();
        Stream stdin = Console.OpenStandardInput();
        if (verbose && logger.RawSink is { } rawSink)
        {
            stdout = new TeeStream(stdout, rawSink);
            stdin = new TeeStream(stdin, rawSink);
        }

        using HeaderDelimitedMessageHandler handler = new(stdout, stdin, formatter);
        JsonRpc rpc = new(handler);
        rpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.StartListening();

        await Task.WhenAny(rpc.Completion, termination.Task).ConfigureAwait(false);

        if (!termination.IsRequested)
        {
            termination.TryRequestAbnormal();
        }

        rpc.Dispose();
        server.Dispose();

        return termination.NormalRequested ? 0 : 1;
    }

    public static void PrintVersion()
    {
        Console.WriteLine($"{ServerMetadata.ServerName} {ServerMetadata.ServerVersion}");
        Console.WriteLine($"tooling {ServerMetadata.ToolingVersion}");
        Console.WriteLine($"compiler-line {ServerMetadata.CompilerCompatibilityLine}");
    }

}
