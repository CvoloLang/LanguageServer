using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

public class CvoloDiagnosticAdapterTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly CvoloLanguageBackend _backend;

    public CvoloDiagnosticAdapterTests()
    {
        _workspace = TestWorkspace.CreateProject(
            ["main.cvl", "lib.cvl"],
            relative => relative == "main.cvl" ? "int Main() { return 0; }" : "int Lib() { return 0; }");
        _backend = new CvoloLanguageBackend([_workspace.DirectoryPath], null, new RecordingCoreLogger());
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private DocumentUri MainUri => DocumentUri.Create(_workspace.PathOf("main.cvl"));

    private DocumentUri LibUri => DocumentUri.Create(_workspace.PathOf("lib.cvl"));

    [Fact]
    public void ValidDocument_ProducesNoDiagnostics()
    {
        var project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project, MainUri);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, "int Main() { return 0; }");

        BackendDiagnosticRun run = _backend.GetDiagnostics(snapshot, [handle]);

        Assert.Empty(run.Diagnostics);
    }

    [Fact]
    public void ParserError_IsReportedForTheCorrectDocument()
    {
        var project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project, MainUri);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, "int Main( { return 0; }");

        BackendDiagnostic diagnostic = Assert.Single(_backend.GetDiagnostics(snapshot, [handle]).Diagnostics);

        Assert.Equal(BackendDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("CVL0000", diagnostic.Code);
        Assert.Equal(new TextSpan(10, 1), diagnostic.Location.Span);
        Assert.True(DocumentUriPathComparer.Instance.Equals(MainUri, diagnostic.Location.Document));
    }

    [Fact]
    public void SemanticError_IsReportedWithItsSpan()
    {
        var project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project, MainUri);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, "int Main() { return nope; }");

        BackendDiagnostic diagnostic = Assert.Single(_backend.GetDiagnostics(snapshot, [handle]).Diagnostics);

        Assert.Equal(BackendDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("nope", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(new TextSpan(20, 4), diagnostic.Location.Span);
    }

    [Fact]
    public void TwoDocuments_FromOneSnapshot_ProduceCoherentTexts()
    {
        var project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle mainHandle = Resolve(project, MainUri);
        BackendDocumentHandle libHandle = Resolve(project, LibUri);
        _backend.UpdateDocument(project, mainHandle, "int Main() { return 0; }");
        BackendSnapshot snapshot = _backend.UpdateDocument(project, libHandle, "int Lib() { return 0; }");

        BackendDiagnosticRun run = _backend.GetDiagnostics(snapshot, [mainHandle, libHandle]);

        Assert.Equal(2, run.DocumentTexts.Count);
        Assert.Equal("int Main() { return 0; }", TextOf(run, MainUri));
        Assert.Equal("int Lib() { return 0; }", TextOf(run, LibUri));
    }

    [Fact]
    public void CrossFileError_IsAttributedToItsOwnDocument()
    {
        var project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle mainHandle = Resolve(project, MainUri);
        BackendDocumentHandle libHandle = Resolve(project, LibUri);
        _backend.UpdateDocument(project, mainHandle, "int Main() { return 0; }");
        BackendSnapshot snapshot = _backend.UpdateDocument(project, libHandle, "int Lib() { return missing; }");

        BackendDiagnostic diagnostic = Assert.Single(_backend.GetDiagnostics(snapshot, [mainHandle, libHandle]).Diagnostics);

        Assert.True(DocumentUriPathComparer.Instance.Equals(LibUri, diagnostic.Location.Document));
    }

    private BackendDocumentHandle Resolve(BackendProject project, DocumentUri uri)
    {
        Assert.True(_backend.TryResolveDocument(project, uri, out BackendDocumentHandle handle));
        return handle;
    }

    private static string TextOf(BackendDiagnosticRun run, DocumentUri uri)
    {
        foreach (var pair in run.DocumentTexts)
        {
            if (DocumentUriPathComparer.Instance.Equals(pair.Key, uri))
            {
                return pair.Value;
            }
        }

        return "<missing>";
    }
}
