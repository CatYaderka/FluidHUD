namespace FluidHUD.Services;

public sealed class ArtworkCache : IDisposable
{
    private sealed record Entry(string Key, byte[] Bytes);

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _lru = new();
    private readonly long _maxBytes;
    private readonly int _maxEntries;
    private long _currentBytes;
    private bool _disposed;

    public ArtworkCache(long maxBytes = 32L * 1024 * 1024, int maxEntries = 40)
    {
        _maxBytes = Math.Max(1024 * 1024, maxBytes);
        _maxEntries = Math.Max(4, maxEntries);
    }

    public bool TryGet(string key, out byte[]? bytes)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(key, out var node))
            {
                bytes = null;
                return false;
            }

            _lru.Remove(node);
            _lru.AddFirst(node);
            bytes = node.Value.Bytes;
            return true;
        }
    }

    public void Put(string key, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0 || bytes.LongLength > _maxBytes)
        {
            return;
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_entries.Remove(key, out var existing))
            {
                _lru.Remove(existing);
                _currentBytes -= existing.Value.Bytes.LongLength;
            }

            var node = new LinkedListNode<Entry>(new Entry(key, bytes));
            _lru.AddFirst(node);
            _entries[key] = node;
            _currentBytes += bytes.LongLength;

            while (_currentBytes > _maxBytes || _entries.Count > _maxEntries)
            {
                var last = _lru.Last;
                if (last is null) break;
                _lru.RemoveLast();
                _entries.Remove(last.Value.Key);
                _currentBytes -= last.Value.Bytes.LongLength;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _entries.Clear();
            _lru.Clear();
            _currentBytes = 0;
        }
    }
}
