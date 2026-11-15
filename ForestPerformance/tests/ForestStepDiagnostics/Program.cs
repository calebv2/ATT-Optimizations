using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using ForestStepDiagnostics;
using HarmonyLib;

internal struct FakeStep
{
    public int state;
    public bool fail;
    internal static readonly Exception Sentinel = new InvalidOperationException("sentinel");
    [MethodImpl(MethodImplOptions.NoInlining)] public void MoveNext() { Thread.Sleep(35); state++; if (fail) throw Sentinel; }
}
internal static class FakeAsync
{
    internal static readonly Exception Sentinel = new InvalidOperationException("async sentinel");
    [MethodImpl(MethodImplOptions.NoInlining)] public static async Task<int> Work(bool fail)
    { if (FineProbe.Current.Id == 0) throw new Exception("missing initial scope"); Thread.Sleep(35); await Task.Yield(); if (FineProbe.Current.Id == 0) throw new Exception("missing resumed scope"); Thread.Sleep(35); if (fail) throw Sentinel; return 42; }
}
internal sealed class FakeSync
{
    internal static readonly Exception Sentinel = new InvalidOperationException("sync sentinel");
    [MethodImpl(MethodImplOptions.NoInlining)] public int Work(bool fail) { Thread.Sleep(35); if (fail) throw Sentinel; return 17; }
}
internal static class Test
{
    static void Set(object o, string n, object? v) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(o, v);
    static void Main()
    {
        var root = Assembly.LoadFrom(Path.Combine(Environment.GetEnvironmentVariable("ATT_GAME_PATH")!, "A Township Tale_Data/Managed/Root.Township.dll"));
        var resolved = StepProbe.Specs.Select(s => StepProbe.Resolve(root, s)).ToArray();
        if (resolved.Length != 10) throw new Exception("missing targets");
        string dir = Path.Combine(Path.GetTempPath(), "forest-step-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "steps.csv");
        var harmony = new HarmonyLib.Harmony("ATT.ForestStepDiagnostics.Smoke");
        try
        {
            using (var sink = new ServerTrace(path, "header"))
            {
                StepProbe.Sink = sink; StepProbe.MainThreadId = Thread.CurrentThread.ManagedThreadId;
                FineProbe.Initialize(dir);
                string hookFile = Path.Combine(dir, "hooks.csv");
                File.WriteAllText(hookFile, "stage,status,detail\n");
                FineProbe.Install(root, harmony, hookFile);
                var hooks = File.ReadAllLines(hookFile);
                if (hooks.Length != 6 || hooks.Skip(1).Any(s => !s.Contains(",attached,"))) throw new Exception("fine hook failed: " + string.Join("|", hooks));
                var fineMethod = typeof(FakeSync).GetMethod(nameof(FakeSync.Work))!;
                var fineInfo = new FineProbe.Info("fixture scoped", true);
                FineProbe.Targets.TryAdd(fineMethod, fineInfo);
                harmony.Patch(fineMethod, prefix: new HarmonyMethod(typeof(FineProbe), nameof(FineProbe.StartScoped)), finalizer: new HarmonyMethod(typeof(FineProbe), nameof(FineProbe.End)));
                var sync = new FakeSync();
                if (sync.Work(false) != 17 || fineInfo.Calls != 0) throw new Exception("unscoped call changed or recorded");
                var previous = FineProbe.Enter("outer", "forest-chunk", out long outerId);
                try {
                    if (Task.Run(() => FineProbe.Current.Id).GetAwaiter().GetResult() != 0) throw new Exception("scope leaked to worker");
                    if (sync.Work(false) != 17 || fineInfo.Calls != 1) throw new Exception("scoped result or counter changed");
                    var nested = FineProbe.Enter("nested", "nested-chunk", out long nestedId);
                    if (FineProbe.Current.Id != nestedId) throw new Exception("nested scope missing");
                    FineProbe.Exit(nested);
                    if (FineProbe.Current.Id != outerId) throw new Exception("nested scope not restored");
                    try { sync.Work(true); throw new Exception("sync exception swallowed"); }
                    catch (InvalidOperationException e) { if (!ReferenceEquals(e, FakeSync.Sentinel)) throw new Exception("sync exception changed"); }
                } finally { FineProbe.Exit(previous); }
                if (FineProbe.Current.Id != 0) throw new Exception("scope not cleared");
                const int fastIterations = 100000;
                var fastClock = Stopwatch.StartNew();
                for (int i = 0; i < fastIterations; i++) { FineProbe.StartScoped(fineMethod, out var unused); FineProbe.End(unused, null); }
                fastClock.Stop();
                Console.WriteLine("Unscoped terrain prefix/finalizer: " + (fastClock.Elapsed.TotalMilliseconds * 1000 / fastIterations).ToString("F3") + " us/call, excluding Harmony detour.");
                foreach (var info in resolved)
                {
                    StepProbe.Targets.TryAdd(info.Step, info);
                    harmony.Patch(info.Step, prefix: new HarmonyMethod(typeof(StepProbe).GetMethod(nameof(StepProbe.Start), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(info.Step.DeclaringType!)), finalizer: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.End)));
                    Console.WriteLine("Installed metadata resolved and attached: " + info.Stage + " " + info.Step.DeclaringType!.FullName);
                }
                var direct = resolved.Single(x => x.Stage == "ForestGenerator.RequestStatic");
                object frame = FormatterServices.GetUninitializedObject(direct.Step.DeclaringType!);
                object chunk = FormatterServices.GetUninitializedObject(root.GetType("Alta.Chunks.LocationChunk", true)!);
                root.GetType("Alta.Chunks.Chunk", true)!.GetField("chunkIdentifier", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(chunk, "forest-test-chunk");
                Set(frame, "chunk", chunk); Set(frame, "<>1__state", 3);
                var directPrefix = typeof(StepProbe).GetMethod(nameof(StepProbe.Start), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(frame.GetType());
                object?[] prefixArgs = { direct.Step, frame, null }; directPrefix.Invoke(null, prefixArgs);
                var ctx = (StepProbe.State)prefixArgs[2]!; Thread.Sleep(35); StepProbe.End(ctx, null);
                if (ctx.Chunk != "forest-test-chunk" || ctx.Before != 3) throw new Exception("chunk context missing");
                var method = typeof(FakeStep).GetMethod(nameof(FakeStep.MoveNext))!;
                var infoFake = new StepProbe.Info(method, "fake", typeof(FakeStep).GetField("state")!, null, null, null, null);
                StepProbe.Targets.TryAdd(method, infoFake);
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(StepProbe).GetMethod(nameof(StepProbe.Start), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(FakeStep))), finalizer: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.End)));
                var fake = new FakeStep();
                var allocationMethod = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", BindingFlags.Static | BindingFlags.Public);
                var allocated = allocationMethod == null ? null : (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), allocationMethod);
                long bytesBefore = allocated?.Invoke() ?? 0; var clock = Stopwatch.StartNew(); const int iterations = 100000;
                for (int i = 0; i < iterations; i++) { StepProbe.Start<FakeStep>(method, ref fake, out var sample); StepProbe.End(sample, null); }
                clock.Stop(); long bytesAfter = allocated?.Invoke() ?? 0;
                Console.WriteLine("Synthetic short-step probe: " + (clock.Elapsed.TotalMilliseconds * 1000 / iterations).ToString("F2") + " us/call, " + (allocated == null ? "allocation unavailable" : ((bytesAfter - bytesBefore) / (double)iterations).ToString("F1") + " bytes/call") + ", excluding Harmony detour.");
                fake.MoveNext();
                if (fake.state != 1) throw new Exception("struct mutation changed");
                fake.fail = true;
                try { fake.MoveNext(); throw new Exception("exception swallowed"); }
                catch (InvalidOperationException e) { if (!ReferenceEquals(e, FakeStep.Sentinel)) throw new Exception("exception changed"); }
                if (fake.state != 2) throw new Exception("exception mutation changed");
                var asyncMethod = typeof(FakeAsync).GetMethod(nameof(FakeAsync.Work))!;
                var asyncState = asyncMethod.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
                if (!asyncState.IsValueType) throw new Exception("Expected compiler generated struct");
                var asyncMove = asyncState.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
                var asyncInfo = new StepProbe.Info(asyncMove, "ForestGenerator.GenerateTerrain.Generate", asyncState.GetField("<>1__state", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!, null, null, null, null);
                StepProbe.Targets.TryAdd(asyncMove, asyncInfo);
                harmony.Patch(asyncMove, prefix: new HarmonyMethod(typeof(StepProbe).GetMethod(nameof(StepProbe.Start), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(asyncState)), finalizer: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.End)));
                if (FakeAsync.Work(false).GetAwaiter().GetResult() != 42) throw new Exception("async result changed");
                try { FakeAsync.Work(true).GetAwaiter().GetResult(); throw new Exception("async exception swallowed"); }
                catch (InvalidOperationException e) { if (!ReferenceEquals(e, FakeAsync.Sentinel)) throw new Exception("async exception changed"); }
                if (asyncInfo.Calls < 4) throw new Exception("async resume steps not observed");
                if (FineProbe.Current.Id != 0) throw new Exception("async scope leaked on caller");
                harmony.UnpatchSelf(); StepProbe.Sink = null; FineProbe.Dispose();
            }
            string[] rows = File.ReadAllLines(path);
            if (rows.Length < 8 || !rows[1].Contains(",ForestGenerator.RequestStatic,") || !rows[1].Contains(",forest-test-chunk,3,") || !rows[3].Contains("System.InvalidOperationException")) throw new Exception("unexpected output: " + string.Join("|", rows));
            string[] fineRows = File.ReadAllLines(Path.Combine(dir, "fine-steps.csv"));
            if (fineRows.Length != 3 || !fineRows[1].Contains(",outer,forest-chunk,") || !fineRows[2].Contains("System.InvalidOperationException")) throw new Exception("fine scope output incorrect: " + string.Join("|", fineRows));
            Console.WriteLine("Actual metadata and chunk extraction passed; struct Harmony detour preserved mutations and exception identity.");
        }
        finally { harmony.UnpatchSelf(); StepProbe.Sink = null; FineProbe.Dispose(); Directory.Delete(dir, true); }
    }
}
