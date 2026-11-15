using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PathfindingQueueDiagnostics
{
    internal sealed class RateGate
    {
        private readonly object gate = new object();
        private readonly int limit;
        private double since = -1000;
        private int count;
        internal RateGate(int limit) { this.limit = limit; }
        internal bool Take(double now)
        {
            lock (gate)
            {
                if (now < since || now - since >= 1000) { since = now; count = 0; }
                return count++ < limit;
            }
        }
    }
    // Weak, bounded generation cache. An old FIFO entry must not remove a newer
    // binding when the game reuses the same pooled Path object.
    internal sealed class GenerationCache<TKey, TValue> where TKey : class where TValue : class
    {
        private sealed class Entry
        {
            internal WeakReference<TKey> Key = null!;
            internal WeakReference<TValue> Value = null!;
        }
        private readonly ConditionalWeakTable<TKey, TValue> map = new ConditionalWeakTable<TKey, TValue>();
        private readonly Queue<Entry> fifo = new Queue<Entry>();
        private readonly int capacity;
        internal long Evictions;
        internal GenerationCache(int capacity) { this.capacity = capacity; }
        // Binding happens on the main thread; reads may occur on path workers.
        internal void Bind(TKey key, TValue value)
        {
            map.Remove(key); map.Add(key, value);
            fifo.Enqueue(new Entry { Key = new WeakReference<TKey>(key), Value = new WeakReference<TValue>(value) });
            while (fifo.Count > capacity)
            {
                var old = fifo.Dequeue();
                if (old.Key.TryGetTarget(out var k) && old.Value.TryGetTarget(out var v) && map.TryGetValue(k, out var current) && ReferenceEquals(v, current))
                { map.Remove(k); Evictions++; }
            }
        }
        internal bool TryGet(TKey key, out TValue value) => map.TryGetValue(key, out value);
        internal void Invalidate(TKey key) => map.Remove(key);
    }
    internal sealed class Span
    {
        [ThreadStatic] internal static Span? Current;
        private static long next;
        internal readonly long Id = Interlocked.Increment(ref next);
        internal readonly Span? Parent;
        internal readonly string Stage;
        internal double Start;
        internal double Children, Waits;
        internal bool Ended;
        internal readonly bool Root;
        internal Span(string stage, bool root, double now)
        { Stage = stage; Root = root; Start = now; Parent = Current; Current = this; }
        internal static Span? Begin(string stage, bool root, double now)
        {
            if (!root && Current == null) return null;
            int depth = 0;
            for (var p = Current; p != null; p = p.Parent) if (++depth >= 32) return null;
            return new Span(stage, root, now);
        }
        internal (double elapsed, double self, double knownWait) End(double now)
        {
            if (Ended) throw new InvalidOperationException("Diagnostic span ended twice");
            Ended = true;
            double elapsed = Math.Max(0, now - Start);
            if (!ReferenceEquals(Current, this)) throw new InvalidOperationException("Diagnostic span order mismatch");
            Current = Parent;
            bool wait = Stage == "receiver_sleep" || Stage == "async_graph_wait";
            double knownWait = wait ? elapsed : Waits;
            if (Parent != null) { Parent.Children += elapsed; Parent.Waits += knownWait; }
            return (elapsed, Math.Max(0, elapsed - Children), knownWait);
        }
    }
}
