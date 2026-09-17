namespace Cvolo.LanguageServer.Tests.TestSupport;

internal static class TestTasks
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<T> WithTimeout<T>(this Task<T> task, string operation, TimeSpan? timeout = null)
    {
        var effective = timeout ?? DefaultTimeout;
        try
        {
            return await task.WaitAsync(effective).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timed out after {effective.TotalSeconds:0.#}s waiting for: {operation}");
        }
    }

    public static async Task WithTimeout(this Task task, string operation, TimeSpan? timeout = null)
    {
        var effective = timeout ?? DefaultTimeout;
        try
        {
            await task.WaitAsync(effective).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Timed out after {effective.TotalSeconds:0.#}s waiting for: {operation}");
        }
    }
}