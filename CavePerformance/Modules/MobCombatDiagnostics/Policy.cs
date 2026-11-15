using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace MobCombatDiagnostics
{
    internal sealed class RateGate
    {
        private double started = double.NegativeInfinity;
        private int used;
        private readonly int maximum;
        internal RateGate(int maximum) { this.maximum = maximum; }
        internal bool Take(double now)
        {
            if (now < started || now - started >= 1000) { started = now; used = 0; }
            if (used >= maximum) return false;
            used++; return true;
        }
    }
    internal sealed class Token
    {
        private static long next;
        public readonly long Id = Interlocked.Increment(ref next);
        public Token() { }
        internal readonly RateGate TransportRate = new RateGate(2);
        internal double RelevantUntil;
        internal long TransportCalls;
    }
    internal sealed class Packet
    {
        private static long next;
        internal readonly long Id = Interlocked.Increment(ref next);
        internal readonly long Peer;
        internal readonly object Writer;
        internal readonly double Started;
        internal bool Relevant;
        internal Packet(object writer, long peer, double started) { Writer = writer; Peer = peer; Started = started; }
    }
    internal sealed class PacketScopes
    {
        [ThreadStatic] internal static Packet? Current;
        private readonly ConditionalWeakTable<object, Packet> writers = new ConditionalWeakTable<object, Packet>();
        internal void Remember(Packet packet) { writers.Remove(packet.Writer); writers.Add(packet.Writer, packet); }
        internal Packet? Consume(object writer)
        {
            if (!writers.TryGetValue(writer, out var packet)) return null;
            writers.Remove(writer); return packet;
        }
    }
    internal static class Probe
    {
        internal static long Errors;
        internal static bool CaveLevel(string? chunk, out int level)
        {
            level = -1;
            const string prefix = "Cave Layer ";
            return chunk != null && chunk.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(chunk.Substring(prefix.Length), out level) && level >= 0;
        }
    }
}
