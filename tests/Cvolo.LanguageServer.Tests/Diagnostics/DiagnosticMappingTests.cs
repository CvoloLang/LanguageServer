using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Tests.TestSupport;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

public class DiagnosticMappingTests
{
    private static readonly DocumentUri Main = DocumentUri.Create(Path.Combine(Path.GetTempPath(), "map-main.cvl"));
    private static readonly DocumentUri Other = DocumentUri.Create(Path.Combine(Path.GetTempPath(), "map-other.cvl"));

    [Fact]
    public void Severities_MapToLspValues()
    {
        var payload = Publish(
            [Diag(BackendDiagnosticSeverity.Error), Diag(BackendDiagnosticSeverity.Warning), Diag(BackendDiagnosticSeverity.Info), Diag(BackendDiagnosticSeverity.Hint)]);

        Assert.Equal(DiagnosticSeverity.Error, payload[0].Severity);
        Assert.Equal(DiagnosticSeverity.Warning, payload[1].Severity);
        Assert.Equal(DiagnosticSeverity.Information, payload[2].Severity);
        Assert.Equal(DiagnosticSeverity.Hint, payload[3].Severity);
    }

    [Fact]
    public void CodeSourceAndMessage_AreMapped()
    {
        BackendDiagnostic diagnostic = Diag(BackendDiagnosticSeverity.Error) with
        {
            Code = "CVL0000",
            Message = "Undefined variable 'nope'",
        };

        DiagnosticPayload payload = Assert.Single(Publish([diagnostic]));

        Assert.Equal("CVL0000", payload.Code);
        Assert.Equal("cvolo", payload.Source);
        Assert.Equal("Undefined variable 'nope'", payload.Message);
    }

    [Fact]
    public void PrimaryLocationMessage_IsIgnored()
    {
        BackendDiagnostic diagnostic = Diag(BackendDiagnosticSeverity.Error) with
        {
            Message = "top level",
            Location = new BackendDiagnosticLocation(Main, new TextSpan(0, 3), "primary location text"),
        };

        DiagnosticPayload payload = Assert.Single(Publish([diagnostic]));

        Assert.Equal("top level", payload.Message);
    }

    [Fact]
    public void RelatedInformation_OmittedWhenClientDoesNotSupportIt()
    {
        BackendDiagnostic diagnostic = WithRelated();

        DiagnosticPayload payload = Assert.Single(Publish([diagnostic], relatedSupported: false));

        Assert.Null(payload.RelatedInformation);
    }

    [Fact]
    public void RelatedInformation_IncludedWhenClientSupportsIt()
    {
        BackendDiagnostic diagnostic = WithRelated();

        DiagnosticPayload payload = Assert.Single(Publish([diagnostic], relatedSupported: true));

        var related = Assert.Single(payload.RelatedInformation!);
        Assert.Equal(new Uri(Other.LocalPath), related.Location.Uri);
        Assert.Equal("related message", related.Message);
    }

    [Fact]
    public void UnresolvableRelatedLocation_IsSkipped_PrimaryRemains()
    {
        BackendDiagnostic diagnostic = Diag(BackendDiagnosticSeverity.Error) with
        {
            RelatedLocations =
            [
                new BackendDiagnosticLocation(DocumentUri.Create(Path.Combine(Path.GetTempPath(), "missing.cvl")), new TextSpan(0, 1), "gone"),
            ],
        };

        DiagnosticPayload payload = Assert.Single(Publish([diagnostic], relatedSupported: true));

        Assert.Null(payload.RelatedInformation);
    }

    [Fact]
    public void ZeroLengthSpan_MapsToPointRange()
    {
        BackendDiagnostic diagnostic = Diag(BackendDiagnosticSeverity.Error) with
        {
            Location = new BackendDiagnosticLocation(Main, new TextSpan(3, 0), null),
        };

        DiagnosticPayload payload = Assert.Single(Publish([diagnostic]));

        Assert.Equal(payload.Range.Start, payload.Range.End);
        Assert.Equal(0, payload.Range.Start.Line);
        Assert.Equal(3, payload.Range.Start.Character);
    }

    [Fact]
    public void SpanAtEndOfDocument_Maps()
    {
        BackendDiagnostic diagnostic = Diag(BackendDiagnosticSeverity.Error) with
        {
            Location = new BackendDiagnosticLocation(Main, new TextSpan(3, 0), null),
        };

        DiagnosticPayload payload = Assert.Single(Publish([diagnostic], mainText: "abc"));

        Assert.Equal(3, payload.Range.Start.Character);
    }

    [Fact]
    public void NonEmptySpan_MapsToExactRange_PreservingCompilerSpan()
    {
        // A compiler diagnostic reported on the iterator expression `r` must publish exactly
        // that range: not widened to the whole statement and not shifted.
        const string mainText = "int main() {\n    foreach (val item in r) {\n    }\n}\n";
        var start = mainText.IndexOf("in r)", StringComparison.Ordinal) + "in ".Length;
        var lineStart = mainText.LastIndexOf('\n', start) + 1;
        var character = start - lineStart;

        BackendDiagnostic diagnostic = Diag(BackendDiagnosticSeverity.Error) with
        {
            Location = new BackendDiagnosticLocation(Main, new TextSpan(start, 1), null),
        };

        DiagnosticPayload payload = Assert.Single(Publish([diagnostic], mainText: mainText));

        Assert.Equal(1, payload.Range.Start.Line);
        Assert.Equal(character, payload.Range.Start.Character);
        Assert.Equal(1, payload.Range.End.Line);
        Assert.Equal(character + 1, payload.Range.End.Character);
    }

    [Fact]
    public void OutOfRangeSpan_IsSkipped_AndLogged()
    {
        var logger = new RecordingLspLogger();
        BackendDiagnostic diagnostic = Diag(BackendDiagnosticSeverity.Error) with
        {
            Location = new BackendDiagnosticLocation(Main, new TextSpan(0, 99), null),
        };

        IReadOnlyList<PublishDiagnosticsPayload> payloads = Build(new DiagnosticPublisher(logger, new DiagnosticSink()), [diagnostic]);

        Assert.Empty(Assert.Single(payloads).Diagnostics);
        Assert.True(logger.Has("Debug", "out-of-range span"));
    }

    [Fact]
    public void Diagnostics_AreGroupedByPrimaryDocument()
    {
        BackendDiagnostic onOther = Diag(BackendDiagnosticSeverity.Error) with
        {
            Location = new BackendDiagnosticLocation(Other, new TextSpan(0, 1), null),
        };

        DiagnosticRunContext context = Context(Main, Other);
        var run = new BackendDiagnosticRun(
            new Dictionary<DocumentUri, string>(DocumentUriPathComparer.Instance)
            {
                [Main] = "abc",
                [Other] = "xy",
            },
            [Diag(BackendDiagnosticSeverity.Error), onOther]);

        IReadOnlyList<PublishDiagnosticsPayload> payloads = new DiagnosticPublisher(new RecordingLspLogger(), new DiagnosticSink()).BuildPayloads(context, run);

        Assert.Equal(2, payloads.Count);
        Assert.Single(payloads.Single(p => p.Uri == new Uri(Main.LocalPath)).Diagnostics);
        Assert.Single(payloads.Single(p => p.Uri == new Uri(Other.LocalPath)).Diagnostics);
    }

    private static IReadOnlyList<DiagnosticPayload> Publish(IReadOnlyList<BackendDiagnostic> diagnostics, bool relatedSupported = false, string mainText = "abcdef")
    {
        var sink = new DiagnosticSink();
        sink.SetRelatedInformationSupported(relatedSupported);
        IReadOnlyList<PublishDiagnosticsPayload> payloads = Build(new DiagnosticPublisher(new RecordingLspLogger(), sink), diagnostics, mainText);
        return Assert.Single(payloads).Diagnostics;
    }

    private static IReadOnlyList<PublishDiagnosticsPayload> Build(DiagnosticPublisher publisher, IReadOnlyList<BackendDiagnostic> diagnostics, string mainText = "abcdef")
    {
        var run = new BackendDiagnosticRun(
            new Dictionary<DocumentUri, string>(DocumentUriPathComparer.Instance) { [Main] = mainText, [Other] = "xy" },
            diagnostics);
        return publisher.BuildPayloads(Context(Main), run);
    }

    private static DiagnosticRunContext Context(params DocumentUri[] uris)
    {
        var targets = new List<DiagnosticPublishTarget>();
        foreach (DocumentUri uri in uris)
        {
            targets.Add(new DiagnosticPublishTarget(uri, DocumentSessionId.Next(), new DocumentVersion(1), new FakeHandle()));
        }

        return new DiagnosticRunContext(new FakeProject(), new FakeSnapshot(), targets);
    }

    private static BackendDiagnostic Diag(BackendDiagnosticSeverity severity)
    {
        return new BackendDiagnostic(
            severity,
            "CVL0000",
            "message",
            new BackendDiagnosticLocation(Main, new TextSpan(0, 3), null),
            []);
    }

    private static BackendDiagnostic WithRelated()
    {
        return Diag(BackendDiagnosticSeverity.Error) with
        {
            RelatedLocations =
            [
                new BackendDiagnosticLocation(Other, new TextSpan(0, 1), "related message"),
            ],
        };
    }

    private sealed class FakeProject : BackendProject { }

    private sealed class FakeSnapshot : BackendSnapshot { }

    private sealed class FakeHandle : BackendDocumentHandle { }
}
