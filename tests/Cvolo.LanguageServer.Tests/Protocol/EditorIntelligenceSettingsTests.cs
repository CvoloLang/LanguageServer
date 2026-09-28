using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Protocol;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// The settings reader is deliberately conservative: it understands the two shapes a client may
/// send, accepts only real booleans, and never resets a preference the client did not mention (§64,
/// §65).
/// </summary>
public class EditorIntelligenceSettingsTests
{
    [Fact]
    public void Defaults_MatchTheRecommendedSet()
    {
        EditorIntelligenceSettings settings = EditorIntelligenceSettings.Default;

        Assert.True(settings.CodeLens.References);
        Assert.True(settings.CodeLens.Layout);
        Assert.False(settings.CodeLens.Members);
        Assert.True(settings.CodeLens.NativeInterop);

        Assert.True(settings.InlayHints.Types);
        Assert.True(settings.InlayHints.Parameters);
        Assert.True(settings.InlayHints.ReceiverMutability);
        Assert.False(settings.InlayHints.Layout);
        Assert.False(settings.InlayHints.EnumValues);
        Assert.False(settings.InlayHints.GenericArguments);
    }

    [Fact]
    public void FullyQualifiedKeys_AreUnderstood()
    {
        EditorIntelligenceSettings settings = EditorIntelligenceSettings.FromConfiguration(
            new JObject
            {
                ["cvolo.codeLens.references"] = false,
                ["cvolo.codeLens.members"] = true,
                ["cvolo.inlayHints.layout"] = true,
                ["cvolo.inlayHints.genericArguments"] = true,
            },
            EditorIntelligenceSettings.Default);

        Assert.False(settings.CodeLens.References);
        Assert.True(settings.CodeLens.Members);
        Assert.True(settings.InlayHints.Layout);
        Assert.True(settings.InlayHints.GenericArguments);
    }

    [Fact]
    public void NestedSections_AreUnderstood()
    {
        EditorIntelligenceSettings settings = EditorIntelligenceSettings.FromConfiguration(
            new JObject
            {
                ["cvolo"] = new JObject
                {
                    ["codeLens"] = new JObject { ["references"] = false },
                    ["inlayHints"] = new JObject { ["types"] = false },
                },
            },
            EditorIntelligenceSettings.Default);

        Assert.False(settings.CodeLens.References);
        Assert.False(settings.InlayHints.Types);
        Assert.True(settings.CodeLens.Layout);
    }

    [Fact]
    public void KeyNames_AreMatchedWithoutRegardToCase()
    {
        EditorIntelligenceSettings settings = EditorIntelligenceSettings.FromConfiguration(
            new JObject { ["CVOLO"] = new JObject { ["CodeLens"] = new JObject { ["References"] = false } } },
            EditorIntelligenceSettings.Default);

        Assert.False(settings.CodeLens.References);
    }

    [Fact]
    public void AbsentAndUnrelatedSettings_LeaveEveryValueAsItWas()
    {
        EditorIntelligenceSettings current = EditorIntelligenceSettings.FromConfiguration(
            new JObject { ["cvolo.codeLens.references"] = false },
            EditorIntelligenceSettings.Default);

        EditorIntelligenceSettings settings = EditorIntelligenceSettings.FromConfiguration(
            new JObject { ["editor.fontSize"] = 14, ["cvolo.trace.server"] = "off" },
            current);

        Assert.Equal(current, settings);
    }

    [Theory]
    [InlineData("off")]
    [InlineData(0)]
    [InlineData(null)]
    public void AValueThatIsNotABoolean_IsTreatedAsAbsent(object? value)
    {
        JObject payload = value is null
            ? new JObject { ["cvolo.codeLens.references"] = JValue.CreateNull() }
            : new JObject { ["cvolo.codeLens.references"] = JToken.FromObject(value) };

        EditorIntelligenceSettings settings = EditorIntelligenceSettings.FromConfiguration(
            payload,
            EditorIntelligenceSettings.Default);

        Assert.True(settings.CodeLens.References);
        Assert.Equal(EditorIntelligenceSettings.Default, settings);
    }

    [Fact]
    public void NoSettingsPayload_KeepsTheCurrentSettings()
    {
        Assert.Equal(
            EditorIntelligenceSettings.Default,
            EditorIntelligenceSettings.FromConfiguration(null, EditorIntelligenceSettings.Default));
    }

    [Fact]
    public void OnlyTheSentSettingsChange_AndTheRestKeepTheirValue()
    {
        EditorIntelligenceSettings first = EditorIntelligenceSettings.FromConfiguration(
            new JObject { ["cvolo.inlayHints.parameters"] = false },
            EditorIntelligenceSettings.Default);

        EditorIntelligenceSettings second = EditorIntelligenceSettings.FromConfiguration(
            new JObject { ["cvolo.inlayHints.enumValues"] = true },
            first);

        Assert.False(second.InlayHints.Parameters);
        Assert.True(second.InlayHints.EnumValues);
        Assert.True(second.InlayHints.Types);
    }

    [Fact]
    public void AnUnchangedPayload_EqualsTheCurrentSettings_SoNoRefreshIsDue()
    {
        EditorIntelligenceSettings current = EditorIntelligenceSettings.FromConfiguration(
            new JObject { ["cvolo.codeLens.references"] = false },
            EditorIntelligenceSettings.Default);

        EditorIntelligenceSettings again = EditorIntelligenceSettings.FromConfiguration(
            new JObject { ["cvolo.codeLens.references"] = false },
            current);

        Assert.Equal(current, again);
    }

    [Fact]
    public void RepeatedContainerKeys_DoNotLoop()
    {
        // A client that nests "cvolo" inside itself must neither hang the notification nor silently
        // read a preference out of an unexpected depth.
        EditorIntelligenceSettings settings = EditorIntelligenceSettings.FromConfiguration(
            new JObject
            {
                ["cvolo"] = new JObject
                {
                    ["cvolo"] = new JObject
                    {
                        ["cvolo"] = new JObject { ["codeLens"] = new JObject { ["references"] = false } },
                    },
                },
            },
            EditorIntelligenceSettings.Default);

        Assert.True(settings.CodeLens.References);
    }

    [Fact]
    public void BackendDefaults_AgreeWithTheDocumentedSet()
    {
        Assert.True(BackendCodeLensOptions.Default.References);
        Assert.False(BackendCodeLensOptions.Default.Members);
        Assert.True(BackendInlayHintOptions.Default.ReceiverMutability);
        Assert.False(BackendInlayHintOptions.Default.EnumValues);
    }
}
