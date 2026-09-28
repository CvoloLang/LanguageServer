using Cvolo.LanguageServer.Core.Backend;
using Newtonsoft.Json.Linq;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// The presentation settings the editor owns (§64). They select which annotations are computed and
/// presented; they are never project semantics, so changing one takes effect through a
/// configuration notification and a refresh, without restarting the session (§65). A setting the
/// client did not send keeps its current value: the server never resets a preference the user has
/// already chosen.
/// </summary>
internal sealed record EditorIntelligenceSettings
{
    /// <summary>The recommended default set.</summary>
    public static EditorIntelligenceSettings Default { get; } = new();

    /// <summary>Which CodeLens categories are computed and presented.</summary>
    public BackendCodeLensOptions CodeLens { get; init; } = BackendCodeLensOptions.Default;

    /// <summary>Which inlay-hint categories are computed and presented.</summary>
    public BackendInlayHintOptions InlayHints { get; init; } = BackendInlayHintOptions.Default;

    /// <summary>
    /// Reads the editor intelligence settings out of a <c>workspace/didChangeConfiguration</c>
    /// payload. Both shapes a client may send are accepted: the fully qualified dotted keys and the
    /// nested <c>cvolo</c> section. Anything absent or not a boolean is left as it is.
    /// </summary>
    public static EditorIntelligenceSettings FromConfiguration(JObject? settings, EditorIntelligenceSettings current)
    {
        if (settings is null)
        {
            return current;
        }

        return new EditorIntelligenceSettings
        {
            CodeLens = ApplyCodeLens(settings, current.CodeLens),
            InlayHints = ApplyInlayHints(settings, current.InlayHints),
        };
    }

    private static BackendCodeLensOptions ApplyCodeLens(JObject settings, BackendCodeLensOptions current)
    {
        return new BackendCodeLensOptions
        {
            References = ReadBool(settings, "codeLens", "references") ?? current.References,
            Layout = ReadBool(settings, "codeLens", "layout") ?? current.Layout,
            Members = ReadBool(settings, "codeLens", "members") ?? current.Members,
            NativeInterop = ReadBool(settings, "codeLens", "nativeInterop") ?? current.NativeInterop,
        };
    }

    private static BackendInlayHintOptions ApplyInlayHints(JObject settings, BackendInlayHintOptions current)
    {
        return new BackendInlayHintOptions
        {
            Types = ReadBool(settings, "inlayHints", "types") ?? current.Types,
            Parameters = ReadBool(settings, "inlayHints", "parameters") ?? current.Parameters,
            ReceiverMutability = ReadBool(settings, "inlayHints", "receiverMutability") ?? current.ReceiverMutability,
            Layout = ReadBool(settings, "inlayHints", "layout") ?? current.Layout,
            EnumValues = ReadBool(settings, "inlayHints", "enumValues") ?? current.EnumValues,
            GenericArguments = ReadBool(settings, "inlayHints", "genericArguments") ?? current.GenericArguments,
        };
    }

    /// <summary>
    /// Reads one boolean setting, trying the fully qualified key first, then the section object the
    /// client may already have nested under <c>cvolo</c>. <paramref name="remaining"/> bounds how
    /// many such container levels are unwrapped, so a self-referential payload cannot loop.
    /// </summary>
    private static bool? ReadBool(JObject settings, string section, string name, int remaining = 2)
    {
        if (TryReadToken(settings, $"cvolo.{section}.{name}", out JToken? qualified))
        {
            return AsBool(qualified);
        }

        if (TryReadToken(settings, section, out JToken? sectionToken)
            && sectionToken is JObject nested
            && TryReadToken(nested, name, out JToken? nestedToken))
        {
            return AsBool(nestedToken);
        }

        if (remaining > 0
            && TryReadToken(settings, "cvolo", out JToken? container)
            && container is JObject scoped)
        {
            return ReadBool(scoped, section, name, remaining - 1);
        }

        return null;
    }

    private static bool TryReadToken(JObject container, string key, out JToken? token)
    {
        JToken? exact = container[key];
        if (exact is not null)
        {
            token = exact;
            return true;
        }

        foreach (JProperty property in container.Properties())
        {
            if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                token = property.Value;
                return true;
            }
        }

        token = null;
        return false;
    }

    /// <summary>
    /// Only a real JSON boolean is accepted. A value of another type is treated as absent rather
    /// than guessed, so a malformed configuration notification cannot silently flip a default.
    /// </summary>
    private static bool? AsBool(JToken? token) => token?.Type == JTokenType.Boolean ? token.Value<bool>() : null;
}
