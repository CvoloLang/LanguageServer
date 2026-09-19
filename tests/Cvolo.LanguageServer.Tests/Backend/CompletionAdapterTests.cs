using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

public class CompletionAdapterTests : IDisposable
{
    private readonly TestWorkspace _workspace;
    private readonly RecordingCoreLogger _logger;
    private readonly CvoloLanguageBackend _backend;

    public CompletionAdapterTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"]);
        _logger = new RecordingCoreLogger();
        _backend = new CvoloLanguageBackend([_workspace.DirectoryPath], null, _logger);
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private DocumentUri MainUri => DocumentUri.Create(_workspace.PathOf("main.cvl"));

    [Fact]
    public void GetCompletions_MapsToolingKindsAndReplacementSpan()
    {
        const string marked = "struct Point { int color; int count; }\nint main() {\n    val Point p;\n    return p.co|;\n}\n";
        int position = marked.IndexOf('|');
        string text = marked.Remove(position, 1);

        BackendProject project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, text);

        BackendCompletionResult result = _backend.GetCompletions(snapshot, handle, position);

        Assert.Contains(result.Items, item => item.Label == "color" && item.Kind == BackendCompletionKind.StructField);
        Assert.Contains(result.Items, item => item.Label == "count" && item.Kind == BackendCompletionKind.StructField);
        Assert.DoesNotContain(result.Items, item => item.Kind == BackendCompletionKind.Keyword);
        Assert.Equal(new TextSpan(position - 2, 2), result.ReplacementSpan);
    }

    [Fact]
    public void GetCompletions_MapsKeywordAndEnumVariantKinds()
    {
        const string marked = "enum Color { Red, Green }\nint main() {\n    ret|urn Color.R;\n}\n";
        int position = marked.IndexOf('|');
        string text = marked.Remove(position, 1);

        BackendProject project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, text);

        BackendCompletionResult result = _backend.GetCompletions(snapshot, handle, position);

        Assert.Contains(result.Items, item => item.Label == "return" && item.Kind == BackendCompletionKind.Keyword);
    }

    [Fact]
    public void GetCompletions_OutOfRangePosition_ThrowsInsteadOfFabricatingSpan()
    {
        BackendProject project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, "int main() { return 0; }\n");

        Assert.Throws<ArgumentOutOfRangeException>(() => _backend.GetCompletions(snapshot, handle, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => _backend.GetCompletions(snapshot, handle, 9999));
    }

    [Fact]
    public void GetCompletions_HandleNotInSnapshot_Throws()
    {
        // A handle minted by another workspace has a structurally distinct
        // DocumentId, so it is absent from this snapshot.
        using var other = TestWorkspace.CreateProject(["other.cvl"]);
        var otherBackend = new CvoloLanguageBackend([other.DirectoryPath], null, _logger);
        var otherUri = DocumentUri.Create(other.PathOf("other.cvl"));
        BackendProject otherProject = otherBackend.OpenProject(otherUri)!;
        Assert.True(otherBackend.TryResolveDocument(otherProject, otherUri, out BackendDocumentHandle foreignHandle));

        BackendProject project = _backend.OpenProject(MainUri)!;
        BackendDocumentHandle handle = Resolve(project);
        BackendSnapshot snapshot = _backend.UpdateDocument(project, handle, "int main() { return 0; }\n");

        Assert.Throws<InvalidOperationException>(() => _backend.GetCompletions(snapshot, foreignHandle, 0));
    }

    [Theory]
    [InlineData(-1, 0, 0, 10, false)] // start < 0
    [InlineData(0, -1, 0, 10, false)] // length < 0
    [InlineData(0, 11, 0, 10, false)] // end beyond text
    [InlineData(5, 0, 0, 10, false)] // starts after cursor
    [InlineData(0, 2, 5, 10, false)] // ends before cursor
    [InlineData(0, 0, 0, 10, true)] // zero-length at cursor
    [InlineData(0, 2, 2, 10, true)] // cursor at prefix end
    [InlineData(2, 3, 3, 10, true)] // cursor inside span
    [InlineData(0, 10, 10, 10, true)] // whole text, cursor at end
    public void IsValidReplacementSpan_EnforcesBoundsAndCursorContainment(int start, int length, int position, int textLength, bool expected)
    {
        Assert.Equal(expected, CvoloLanguageBackend.IsValidReplacementSpan(start, length, position, textLength));
    }

    private BackendDocumentHandle Resolve(BackendProject project)
    {
        Assert.True(_backend.TryResolveDocument(project, MainUri, out BackendDocumentHandle handle));
        return handle;
    }
}
