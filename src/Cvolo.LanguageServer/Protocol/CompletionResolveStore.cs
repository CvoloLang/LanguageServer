using Cvolo.LanguageServer.Core;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Documents;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// Session-scoped, bounded store mapping opaque completion-item data tokens to the resolver
/// entry that can fulfil them. A token is only ever the server-generated key returned to the
/// client in <c>CompletionItem.data</c>; it never contains backend or compiler identifiers
/// (§26, §27). Entries resolve against the exact snapshot that produced their candidate.
/// </summary>
internal sealed class CompletionResolveStore
{
    public const int DefaultCapacity = 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, CompletionResolveEntry> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _insertionOrder = new();
    private readonly int _capacity;

    public CompletionResolveStore(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Completion resolve store capacity must be positive.");
        }

        _capacity = capacity;
    }

    /// <summary>
    /// Atomically commits a staged completion batch. Returns the set of tokens that were accepted;
    /// tokens past the bounded capacity are not attached to any item. A commit never evicts a token
    /// from its own batch — when the batch alone exceeds capacity only the first entries survive,
    /// and nothing from this batch is dropped to make room for later batch members (§27.3).
    /// </summary>
    public HashSet<string> Commit(IReadOnlyList<StagedResolveItem> staged)
    {
        var attached = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (StagedResolveItem stagedItem in staged)
            {
                if (_entries.ContainsKey(stagedItem.Token))
                    continue;

                if (_entries.Count >= _capacity)
                {
                    if (_insertionOrder.First is not { } oldest || attached.Contains(oldest.Value))
                        break;

                    _entries.Remove(oldest.Value);
                    _insertionOrder.RemoveFirst();
                }

                _entries.Add(stagedItem.Token, stagedItem.Entry);
                _insertionOrder.AddLast(stagedItem.Token);
                attached.Add(stagedItem.Token);
            }

            return attached;
        }
    }

    /// <summary>Looks up the resolve entry for an opaque token. Returns false for unknown or evicted tokens.</summary>
    public bool TryGet(string token, out CompletionResolveEntry entry)
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
                return;

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

    /// <summary>Evicts every entry belonging to a closed document.</summary>
    public void EvictForUri(DocumentUri uri)
    {
        lock (_gate)
        {
            var evicted = _entries.Where(pair => pair.Value.Context.Uri == uri).Select(pair => pair.Key).ToArray();
            foreach (string token in evicted)
            {
                _entries.Remove(token);
            }

            if (evicted.Length > 0)
            {
                var survivors = new HashSet<string>(_entries.Keys, StringComparer.Ordinal);
                var node = _insertionOrder.First;
                while (node is not null)
                {
                    var next = node.Next;
                    if (!survivors.Contains(node.Value))
                        _insertionOrder.Remove(node);
                    node = next;
                }
            }
        }
    }
}

/// <summary>One item staged for lazy resolution before the batch is committed.</summary>
internal sealed record StagedResolveItem(string Token, int ItemIndex, CompletionResolveEntry Entry);

/// <summary>A resolver unit: the exact semantic context + backend handle + the effective fields it may fill.</summary>
internal sealed record CompletionResolveEntry(
    SemanticRequestContext Context,
    BackendCompletionResolveHandle Handle,
    BackendCompletionResolvableFields EffectiveResolvableFields);