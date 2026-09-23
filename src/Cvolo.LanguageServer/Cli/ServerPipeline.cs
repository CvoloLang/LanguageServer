#if CVOLO_MANAGED_TOOLING
using Cvolo.Compiler.Tooling;
#endif
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;
using System.Reflection;
using System.Runtime.Loader;

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

        if (!TryLoadTooling(logger))
        {
            return 1;
        }

        TerminationRequest termination = new();
        using IClientProcessWatcher clientWatcher = ClientProcessWatcherFactory.Create();
        using Protocol.LanguageServer server = new(termination, logger, clientWatcher);

        JsonMessageFormatter formatter = new();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
        formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;
        formatter.JsonSerializer.Converters.Add(new UriJsonConverter());
        Stream stdout = Console.OpenStandardOutput();
        Stream stdin = Console.OpenStandardInput();
        if (verbose && logger.RawSink is { } rawSink)
        {
            stdout = new TeeStream(stdout, rawSink);
            stdin = new TeeStream(stdin, rawSink);
        }

        using HeaderDelimitedMessageHandler handler = new(stdout, stdin, formatter);
        JsonRpc rpc = new(handler);
        server.Diagnostics.Attach(rpc);
        server.Refresh.Attach(rpc);
        rpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Sync, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Completion, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.SignatureHelp, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Hover, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Definition, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.DocumentSymbols, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.SemanticTokens, new JsonRpcTargetOptions
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

    private static bool TryLoadTooling(ILspLogger logger)
    {
#if CVOLO_MANAGED_TOOLING
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Cvolo.Compiler.Tooling.dll");
            if (!File.Exists(path))
            {
                logger.Error($"Cvolo.Compiler.Tooling.dll was not found at {path}.");
                return false;
            }

            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            Type? workspaceType = assembly.GetType("Cvolo.Compiler.Tooling.CvoloWorkspace", throwOnError: false);
            if (workspaceType is null)
            {
                logger.Error("Cvolo.Compiler.Tooling loaded but the CvoloWorkspace type was not found.");
                return false;
            }

            if (!ReferenceEquals(workspaceType, typeof(CvoloWorkspace)))
            {
                logger.Error("CvoloWorkspace type identity mismatch; tooling is incompatible with this server.");
                return false;
            }

            logger.Info($"Cvolo.Compiler.Tooling {assembly.GetName().Version} loaded successfully.");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to load Cvolo.Compiler.Tooling: {ex.Message}");
            return false;
        }
#else
        logger.Warning("Cvolo.Compiler.Tooling.dll is unavailable; compiler-backed language features are disabled.");
        return true;
#endif
    }
}