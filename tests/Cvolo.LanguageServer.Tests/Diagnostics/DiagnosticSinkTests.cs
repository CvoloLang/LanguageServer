using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Diagnostics;

public class DiagnosticSinkTests
{
    [Fact]
    public void UriJsonConverter_SerializesWindowsDrivePathAsFileUri()
    {
        var path = @"d:\Programming\CvoloLang\TestCases\SimpleTestCase\Main.cvl";

        var json = JsonConvert.SerializeObject(new Uri(path), new UriJsonConverter());

        Assert.Equal("\"file:///d:/Programming/CvoloLang/TestCases/SimpleTestCase/Main.cvl\"", json);
    }

    [Fact]
    public void UriJsonConverter_ReadsFileUri()
    {
        var uri = JsonConvert.DeserializeObject<Uri>("\"file:///d:/x/Main.cvl\"", new UriJsonConverter());

        Assert.NotNull(uri);
        Assert.True(uri!.IsFile);
    }

    [Fact]
    public async Task ObserveNotification_LogsAsynchronouslyFaultedTask()
    {
        var logger = new RecordingLspLogger();
        var source = new TaskCompletionSource();
        Task notification = source.Task;

        DiagnosticSink.ObserveNotification(notification, logger);

        source.SetException(new InvalidOperationException("async-boom"));

        await WaitUntil(() => logger.Has("Warning", "async-boom"));
    }

    [Fact]
    public async Task ObserveNotification_LogsSynchronouslyFaultedTask()
    {
        var logger = new RecordingLspLogger();
        var notification = Task.FromException(new InvalidOperationException("sync-boom"));

        DiagnosticSink.ObserveNotification(notification, logger);

        await WaitUntil(() => logger.Has("Warning", "sync-boom"));
    }

    [Fact]
    public void ObserveNotification_CompletedSuccessfully_LogsNothing()
    {
        var logger = new RecordingLspLogger();

        DiagnosticSink.ObserveNotification(Task.CompletedTask, logger);

        Assert.False(logger.Has("Warning", "Publishing diagnostics failed"));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for the expected log entry.");
            }

            await Task.Delay(10);
        }
    }
}
