using System;
using System.Collections.Generic;

namespace InfoPanel.Drawing
{
    /// <summary>
    /// Least-recently-used cache of disposable resources, bounded by entry count
    /// and by a byte budget. Evicted and replaced values are disposed. Not thread
    /// safe: callers serialise access.
    /// </summary>
    internal sealed class LruResourceCache<TKey, TValue>(int maxEntries, long maxBytes)
        where TKey : notnull
        where TValue : class, IDisposable
    {
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Bytes)>> _map = new();
        private readonly LinkedList<(TKey Key, TValue Value, long Bytes)> _lru = new();

        public int MaxEntries { get; } = maxEntries;
        public long MaxBytes { get; } = maxBytes;
        public int Count => _map.Count;
        public long Bytes { get; private set; }

        public bool TryGet(TKey key, out TValue value)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                value = node.Value.Value;
                return true;
            }

            value = null!;
            return false;
        }

        /// <summary>
        /// Inserts <paramref name="value"/> as most recently used and evicts the
        /// oldest entries until both limits hold. Returns false, leaving ownership
        /// with the caller, when the value alone exceeds the byte budget.
        /// </summary>
        public bool Add(TKey key, TValue value, long bytes)
        {
            if (bytes > MaxBytes || MaxEntries <= 0)
                return false;

            if (_map.Remove(key, out var existing))
            {
                _lru.Remove(existing);
                Bytes -= existing.Value.Bytes;
                if (!ReferenceEquals(existing.Value.Value, value))
                    existing.Value.Value.Dispose();
            }

            _map[key] =_lru.AddFirst((key, value, bytes));
            Bytes += bytes;

            while (_map.Count > MaxEntries || Bytes > MaxBytes)
            {
                var last = _lru.Last!;
                if (ReferenceEquals(last.Value.Value, value))
                    break;
                Remove(last.Value.Key);
            }

            return true;
        }

        public bool Remove(TKey key)
        {
            if (!_map.Remove(key, out var node))
                return false;

            _lru.Remove(node);
            Bytes -= node.Value.Bytes;
            node.Value.Value.Dispose();
            return true;
        }

        public void Clear()
        {
            foreach (var (_, value, _) in _lru)
                value.Dispose();

            _lru.Clear();
            _map.Clear();
            Bytes = 0;
        }
    }
}
