using Cvolo.LanguageServer.Logging;
using StreamJsonRpc;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// <c>workspace/didChangeConfiguration</c> handler. The editor owns the decoration settings, so a
/// change to one is applied in place and answered with a lens and hint refresh: the annotations on
/// screen change without restarting the session and without regenerating a project snapshot (§65).
/// </summary>
internal sealed class ConfigurationHandler(
    ILspLogger logger,
    Func<Newtonsoft.Json.Linq.JObject, bool> applyConfiguration,
    Action requestRefresh)
{
    [JsonRpcMethod("workspace/didChangeConfiguration", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeConfiguration(DidChangeConfigurationParams? parameters)
    {
        if (parameters?.Settings is not { } settings)
        {
            logger.Debug("[didChangeConfiguration] notification without a settings payload; ignored.");
            return;
        }

        if (!applyConfiguration(settings))
        {
            logger.Debug("[didChangeConfiguration] the editor intelligence settings are unchanged.");
            return;
        }

        logger.Info("Editor intelligence settings changed; requesting a CodeLens and inlay hint refresh.");
        requestRefresh();
    }
}
