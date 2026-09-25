using Cvolo.LanguageServer.Protocol;

namespace Cvolo.LanguageServer.Tests.Protocol;

public sealed class InitializationLibraryPathTests
{
    [Fact]
    public void LibraryPaths_AreCapturedAndDeduplicated()
    {
        var state = new SessionState();
        state.MarkInitializeReceived(new InitializeRequestParams
        {
            InitializationOptions = new CvoloInitializationOptions
            {
                LibraryPaths = [" libs ", "", "libs"],
            },
        });

        string path = Assert.Single(state.LibraryPaths);
        Assert.Equal("libs", path);
    }
}
