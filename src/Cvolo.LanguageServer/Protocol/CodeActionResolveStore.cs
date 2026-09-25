using System.Globalization;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Session-scoped, bounded store mapping opaque code-action data tokens to the resolver entry that
/// can fulfil them. A token is only ever the server-generated key returned to the client in
/// <c>CodeAction.data</c>; it never contains backend or compiler identifiers. Entries are never
/// proactively evicted when a document closes or a generation advances, so a stale action remains
/// distinguishable from an unknown one and resolves to <c>ContentModified</c> rather than
/// <c>RequestFailed</c>.
/// </summary>
internal sealed class CodeActionResolveStore
{
    public const int DefaultCapacity = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, CodeActionResolveEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _insertionOrder = new();
    private readonly int _capacity;
    private long _nextToken;

    public CodeActionResolveStore(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Code action resolve store capacity must be positive.");
        }

        _capacity = capacity;
    }

    /// <summary>The maximum number of retained resolve entries.</summary>
    public int Capacity => _capacity;

    /// <summary>Mints a session-unique opaque token for a staged resolve entry.</summary>
    public string MintToken()
    {
        return Interlocked.Increment(ref _nextToken).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Atomically commits a staged batch. A commit evicts older tokens when the bound is reached but
    /// never evicts a token from its own batch.
    /// </summary>
    public void Commit(IReadOnlyList<StagedCodeActionResolveItem> staged)
    {
        lock (_gate)
        {
            foreach (StagedCodeActionResolveItem item in staged)
            {
                if (_entries.ContainsKey(item.Token))
                {
                    continue;
                }

                while (_entries.Count >= _capacity)
                {
                    if (_insertionOrder.First is not { } oldest)
                    {
                        break;
                    }

                    _entries.Remove(oldest.Value);
                    _insertionOrder.RemoveFirst();
                }

                _entries.Add(item.Token, item.Entry);
                _insertionOrder.AddLast(item.Token);
            }
        }
    }

    /// <summary>Looks up the resolve entry for an opaque token. Returns false for unknown or evicted tokens.</summary>
    public bool TryGet(string token, out CodeActionResolveEntry entry)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(token, out entry!);
        }
    }

    /// <summary>Evicts one entry (e.g. it failed a freshness gate).</summary>
    public void Evict(string token)
    {
        lock (_gate)
        {
            if (!_entries.Remove(token))
            {
                return;
            }

            var node = _insertionOrder.First;
            while (node is not null)
            {
                if (string.Equals(node.Value, token, StringComparison.Ordinal))
                {
                    _insertionOrder.Remove(node);
                    return;
                }

                node = node.Next;
            }
        }
    }

    /// <summary>Discards every entry (session shutdown).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _insertionOrder.Clear();
        }
    }
}

/// <summary>One action staged for lazy resolution before the batch is committed.</summary>
internal sealed record StagedCodeActionResolveItem(string Token, CodeActionResolveEntry Entry);

/// <summary>The exact semantic context and backend handle a lazy action resolves against.</summary>
internal sealed record CodeActionResolveEntry(
    SemanticEditRequestContext Context,
    BackendCodeFixHandle Handle);
