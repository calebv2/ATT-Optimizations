using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using Alta.Networking;
using HarmonyLib;
using MobCombatDiagnostics;
using GameWriter = Alta.Serialization.StreamWriter;

internal sealed class PacketFixture
{
    internal static readonly Exception Sentinel = new InvalidOperationException("fixture original failed");
    internal Action? Nested;
    internal object? LastWriter;
    internal long SeenPacket;
    internal int Calls;
    [MethodImpl(MethodImplOptions.NoInlining)] public bool Write(GameWriter writer, Connection connection, bool fail)
    {
        Calls++; LastWriter = writer;
        SeenPacket = PacketScopes.Current!.Id;
        PacketScopes.Current.Relevant = true;
        Nested?.Invoke();
        if (fail) throw Sentinel;
        return false;
    }
}
internal static class Program
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void PolicyTests()
    {
        var rate = new RateGate(2);
        Check(rate.Take(10) && rate.Take(20) && !rate.Take(30), "rate cap failed");
        Check(!rate.Take(1009) && rate.Take(1010), "rate window changed");
        Check(rate.Take(0), "backwards clock did not reset rate gate");
        Check(Probe.CaveLevel("Cave Layer 178", out var level) && level == 178, "cave parse failed");
        Check(!Probe.CaveLevel("Chunk 1-2", out _) && !Probe.CaveLevel("Cave Layer -1", out _) && !Probe.CaveLevel(null, out _) && !Probe.CaveLevel("Cave Layer 99999999999", out _), "invalid chunk parsed");
        var scopes = new PacketScopes(); var writer = new object();
        var first = new Packet(writer, 1, 1); var next = new Packet(writer, 2, 2);
        scopes.Remember(first); scopes.Remember(next);
        Check(ReferenceEquals(scopes.Consume(writer), next) && scopes.Consume(writer) == null, "reused writer marker stale or reusable");
        var token = new ConditionalWeakTable<object, Token>(); var peer = new object();
        Check(token.GetOrCreateValue(peer).Id == token.GetOrCreateValue(peer).Id && token.GetOrCreateValue(new object()).Id != token.GetOrCreateValue(peer).Id, "peer tokens not stable/opaque");
        Console.WriteLine("PASS policies: rate caps, time reset, cave parsing, opaque tokens and one-use writer correlation.");
    }
    private static void PacketHookTests()
    {
        var patcher = new HarmonyLib.Harmony("ATT.MobCombatDiagnostics.Fixture");
        var other = new HarmonyLib.Harmony("ATT.MobCombatDiagnostics.Other");
        var method = typeof(PacketFixture).GetMethod("Write")!;
        var writer = (GameWriter)FormatterServices.GetUninitializedObject(typeof(GameWriter));
        var connection = (Connection)FormatterServices.GetUninitializedObject(typeof(Connection));
        var prior = new Packet(new object(), 777, 0);
        Core.Ready = true; Core.MainThread = Thread.CurrentThread.ManagedThreadId;
        try
        {
            other.Patch(method, postfix: new HarmonyMethod(typeof(Program), nameof(Observed)));
            patcher.Patch(method, prefix: new HarmonyMethod(typeof(Hooks), "PacketBegin"), finalizer: new HarmonyMethod(typeof(Hooks), "PacketEnd"));
            PacketScopes.Current = prior;
            var fixture = new PacketFixture();
            Check(!fixture.Write(writer, connection, false) && fixture.Calls == 1 && ReferenceEquals(fixture.LastWriter, writer), "actual hooks changed original result/arguments");
            Check(ReferenceEquals(PacketScopes.Current, prior), "scope not restored");
            Check(Core.Packets.Consume(writer)?.Id == fixture.SeenPacket && Core.Packets.Consume(writer) == null, "actual hook writer correlation failed");
            var inner = new PacketFixture();
            var otherWriter = (GameWriter)FormatterServices.GetUninitializedObject(typeof(GameWriter));
            fixture.Nested = () =>
            {
                var outer = PacketScopes.Current;
                inner.Write(otherWriter, connection, false);
                Check(ReferenceEquals(PacketScopes.Current, outer), "nested scope not restored");
            };
            fixture.Write(writer, connection, false);
            Check(fixture.SeenPacket != inner.SeenPacket && ReferenceEquals(PacketScopes.Current, prior), "nested packet IDs collapsed");
            Core.Packets.Consume(writer); Core.Packets.Consume(otherWriter);
            fixture.Nested = null;
            Core.Packets.Remember(new Packet(writer, 999, 0));
            try { fixture.Write(writer, connection, true); throw new Exception("original exception swallowed"); }
            catch (InvalidOperationException e) { Check(ReferenceEquals(e, PacketFixture.Sentinel), "original exception replaced"); }
            Check(ReferenceEquals(PacketScopes.Current, prior) && Core.Packets.Consume(writer) == null, "exception left scope/packet marker");
            long errors = Probe.Errors;
            var broken = new Hooks.Call { Scoped = true, Previous = prior };
            typeof(Hooks).GetMethod("PacketEnd", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object?[] { broken, null });
            Check(Probe.Errors == errors + 1 && ReferenceEquals(PacketScopes.Current, prior), "actual hook diagnostic exception escaped or corrupted scope");
            bool workerPassed = false;
            var worker = new Thread(() =>
            {
                var marker = new Packet(new object(), 0, 0);
                PacketScopes.Current = marker;
                var onWorker = new PacketFixture();
                onWorker.Write(writer, connection, false);
                workerPassed = ReferenceEquals(PacketScopes.Current, marker) && onWorker.SeenPacket == marker.Id;
                PacketScopes.Current = null;
            });
            worker.Start(); worker.Join();
            Check(workerPassed && Core.OffThread > 0 && Core.Packets.Consume(writer) == null, "worker probe touched main-thread correlation");
            Core.Ready = false;
            fixture.Write(writer, connection, false);
            Check(fixture.SeenPacket == prior.Id && ReferenceEquals(PacketScopes.Current, prior), "disabled probe changed correlation");
            patcher.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(method).Owners.Contains(other.Id) && observed > 0, "rollback removed another observer");
            Console.WriteLine("PASS actual packet hooks on Harmony fixture: return/arguments, nested scopes, reusable writer, exception identity, diagnostic failure and independent owner preserved.");
        }
        finally { Core.Ready = false; PacketScopes.Current = null; patcher.UnpatchSelf(); other.UnpatchSelf(); }
    }
    private static int observed;
    private static void Observed() { observed++; }
    private static void InstalledTests()
    {
        Hooks.Validate();
        var target = (HealthObject)FormatterServices.GetUninitializedObject(typeof(HealthObject));
        var field = typeof(HealthObject).GetField("healthStat", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object value = typeof(Hooks).GetMethod("ExistingHealth", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { target })!;
        Check((string)value == "unavailable" && field.GetValue(target) == null, "observer initialized a missing health stat");
        string hooks = Path.GetTempFileName();
        var patcher = new HarmonyLib.Harmony("ATT.MobCombatDiagnostics.Installed");
        try
        {
            Hooks.Attach(patcher, hooks);
            Check(File.ReadAllLines(hooks).Count(x => x.EndsWith(",attached")) == 9, "not all installed targets attached");
            Check(Hooks.Resolve(typeof(Connection), "Send", 3).GetParameters()[1].ParameterType == typeof(GameWriter), "wrong Send overload");
            EntitySerializer.WroteEntity += Hooks.Cached;
            EntitySerializer.WroteEntity -= Hooks.Cached;
            Console.WriteLine("PASS installed game metadata, all nine Harmony targets, WroteEntity subscription, exact Send overload and no lazy health-stat creation. Unity gameplay not executed.");
        }
        finally { patcher.UnpatchSelf(); File.Delete(hooks); }
    }
    private static void WriterTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mob-diag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "test.csv");
            using (var sink = new ServerTrace(file, "a,b"))
            {
                sink.Enqueue(ServerTrace.Row("a,b", "quote\"line\n"));
                sink.Enqueue(new string('x', 16385));
                Check(sink.Dropped == 1, "oversized row not bounded");
            }
            var nonblocking = new ServerTrace(Path.Combine(dir, "runtime-stop.csv"), "x");
            nonblocking.Enqueue("one"); nonblocking.Stop(false);
            nonblocking.Enqueue("after-stop");
            Check(nonblocking.Dropped > 0, "runtime stop still accepted rows");
            for (int n = 0; n < 100 && !File.Exists(Path.Combine(dir, "runtime-stop.csv")); n++) Thread.Sleep(10);
            string text = File.ReadAllText(file);
            Check(text.StartsWith("a,b") && text.Contains("\"a,b\"") && text.Contains("\"quote\"\"line\n\""), "CSV quoting/header failed");
            string blocked = Path.Combine(dir, "blocked"); File.WriteAllText(blocked, "file");
            using (var sink = new ServerTrace(Path.Combine(blocked, "bad.csv"), "x"))
            {
                sink.Enqueue("hello");
                for (int n = 0; n < 100 && sink.Error == null; n++) Thread.Sleep(10);
                Check(sink.Error != null, "writer failure not exposed");
                sink.Enqueue("later"); Check(sink.Dropped > 0, "failed writer lost drop accounting");
            }
            Console.WriteLine("PASS CSV header/quoting, row-size bound, worker flush, I/O failure isolation and drop accounting.");
        }
        finally { Directory.Delete(dir, true); }
    }
    private static void Main() { PolicyTests(); PacketHookTests(); InstalledTests(); WriterTests(); }
}
