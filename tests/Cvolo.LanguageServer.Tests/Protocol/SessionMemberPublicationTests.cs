using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;
using LanguageServerImpl = Cvolo.LanguageServer.Protocol.LanguageServer;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// Session members are built on first use. <c>field ??= factory()</c> is not
/// atomic: two callers can both observe null, both run the factory, and then each
/// returns the instance it built rather than the one that was published, because
/// the compiler lowers the coalescing assignment to a local that is returned
/// directly. Everything a session must share then forks silently: the document
/// store forks into two compiler sessions, the resolve stores fork into two token
/// tables, and the timer-owning handlers fork into two schedulers. These tests
/// pin the invariant that first use publishes exactly one instance and every
/// later caller observes that same instance.
/// </summary>
public class SessionMemberPublicationTests
{
    private const int Racers = 8;
    private const int Attempts = 50;

    [Fact]
    public void ConcurrentFirstUse_OfTheSessionStore_PublishesExactlyOneInstance()
    {
        int created = 0;

        using LanguageServerImpl server = NewServer(() =>
        {
            Interlocked.Increment(ref created);
            // The factory is the only work between the null check and the
            // assignment, so slowing it down is what turns a lost race into an
            // observed one instead of something that is rarely reproduced.
            Thread.SpinWait(50_000);
            return NewTestStore();
        });

        DocumentStore[] observed = Race(() => server.Store);

        Assert.All(observed, store => Assert.NotNull(store));
        Assert.Single(observed.Distinct());
        Assert.Equal(1, Volatile.Read(ref created));
    }

    [Fact]
    public void ConcurrentFirstUse_OfSharedSessionMembers_NeverForksTheInstance()
    {
        Assert.Equal(0, CountForks(server => server.Store));
        Assert.Equal(0, CountForks(server => server.ResolveStore));
        Assert.Equal(0, CountForks(server => server.CodeActionResolveStore));
        Assert.Equal(0, CountForks(server => server.Sync));
        Assert.Equal(0, CountForks(server => server.Refresh));
        Assert.Equal(0, CountForks(server => server.EditorIntelligenceRefresh));
        Assert.Equal(0, CountForks(server => server.Completion));
        Assert.Equal(0, CountForks(server => server.Hover));
        Assert.Equal(0, CountForks(server => server.Definition));
        Assert.Equal(0, CountForks(server => server.SignatureHelp));
        Assert.Equal(0, CountForks(server => server.SemanticTokens));
        Assert.Equal(0, CountForks(server => server.Rename));
    }

    private static LanguageServerImpl NewServer(Func<DocumentStore>? storeFactory = null)
    {
        return new LanguageServerImpl(
            new TerminationRequest(),
            new LspLogger(false, null),
            new FakeClientProcessWatcher(),
            storeFactory);
    }

    private static DocumentStore NewTestStore()
    {
        return new DocumentStore(new BlockingBackend(), new RecordingCoreLogger());
    }

    /// <summary>
    /// Counts how many fresh sessions handed out more than one instance of a
    /// member to callers that asked for it at the same time.
    /// </summary>
    private static int CountForks(Func<LanguageServerImpl, object> read)
    {
        int forks = 0;
        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            using LanguageServerImpl server = NewServer(NewTestStore);
            object[] observed = Race(() => read(server));
            if (observed.Distinct().Count() > 1)
            {
                forks++;
            }
        }

        return forks;
    }

    /// <summary>Releases <see cref="Racers"/> dedicated threads onto one read at the same instant.</summary>
    private static T[] Race<T>(Func<T> read) where T : class
    {
        T?[] observed = new T?[Racers];
        using Barrier start = new(Racers);
        Thread[] racers = new Thread[Racers];

        for (int i = 0; i < Racers; i++)
        {
            int index = i;
            racers[i] = new Thread(() =>
            {
                start.SignalAndWait();
                observed[index] = read();
            })
            {
                IsBackground = true,
                Name = $"session-member-racer-{i}",
            };
            racers[i].Start();
        }

        foreach (Thread racer in racers)
        {
            Assert.True(racer.Join(TimeSpan.FromSeconds(30)), "A session member racer did not finish.");
        }

        return observed!;
    }
}
