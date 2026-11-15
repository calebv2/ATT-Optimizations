using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace ForestStepDiagnostics
{
    // Scope is thread-local and is restored even when an instrumented call throws.
    // It never pretends to flow through an await or to another worker thread.
    internal static class FineProbe
    {
        internal struct Scope { internal long Id; internal string? Stage, Chunk; }
        [ThreadStatic] private static Scope current;
        private static long nextScope, errors;
        private static ServerTrace? sink, health;
        internal static string? Error => sink?.Error ?? health?.Error;
        internal static readonly ConcurrentDictionary<MethodBase, Info> Targets = new ConcurrentDictionary<MethodBase, Info>();
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        internal sealed class Info
        {
            internal readonly string Stage;
            internal readonly bool RequiresScope;
            internal long Calls, Slow;
            internal Info(string stage, bool requiresScope) { Stage = stage; RequiresScope = requiresScope; }
        }
        internal struct State { internal long Started; internal int Thread; internal Scope Scope; internal Info? Info; }
        internal static Scope Enter(string stage, string? chunk, out long id)
        {
            Scope prior = current;
            id = Interlocked.Increment(ref nextScope);
            current = new Scope { Id = id, Stage = stage, Chunk = chunk };
            return prior;
        }
        internal static void Exit(Scope prior) { current = prior; }
        internal static Scope Current => current;
        internal static void Initialize(string dir)
        {
            sink = new ServerTrace(Path.Combine(dir, "fine-steps.csv"), "utc,stage,duration_ms,scope_id,scope_stage,chunk_identifier,managed_thread_id,is_main_thread,exception_type,dropped_rows");
            health = new ServerTrace(Path.Combine(dir, "fine-health.csv"), "utc,stage,calls,slow_steps,probe_errors,fine_writer_error,fine_dropped,health_dropped");
        }
        internal static void Install(Assembly root, HarmonyLib.Harmony harmony, string hookFile)
        {
            Attach(root, harmony, hookFile, "TerrainAccess", "ApplyHeights", 0, "TerrainAccess.ApplyHeights [forest scope]", true, false);
            Attach(root, harmony, hookFile, "TerrainAccess", "ApplySplat", 0, "TerrainAccess.ApplySplat [forest scope]", true, false);
            Attach(root, harmony, hookFile, "TerrainAccess", "SampleSplat", 0, "TerrainAccess.SampleSplat [forest scope]", true, false);
            Attach(root, harmony, hookFile, "Alta.Forests.ForestGeneration", "<SortNodes>b__0", 0, "ForestGeneration.SortNodes.WorkerBuild", false, true);
            Attach(root, harmony, hookFile, "Alta.Forests.ForestGeneration", "<SortNodes>b__1", 0, "ForestGeneration.SortNodes.WorkerMerge", false, true);
        }
        private static void Attach(Assembly root, HarmonyLib.Harmony harmony, string hookFile, string type, string method, int parameters, string stage, bool requiresScope, bool nested)
        {
            try {
                MethodInfo target = Resolve(root, type, method, parameters, nested);
                var info = new Info(stage, requiresScope);
                if (!Targets.TryAdd(target, info)) throw new InvalidOperationException("duplicate hook");
                try { harmony.Patch(target, prefix: new HarmonyMethod(typeof(FineProbe), requiresScope ? nameof(StartScoped) : nameof(Start)), finalizer: new HarmonyMethod(typeof(FineProbe), nameof(End))); }
                catch { Targets.TryRemove(target, out _); throw; }
                File.AppendAllText(hookFile, ServerTrace.Row(stage, "attached", target.DeclaringType!.FullName + "." + target.Name) + "\n");
            } catch (Exception e) { File.AppendAllText(hookFile, ServerTrace.Row(stage, "unavailable", e.GetType().Name + ": " + e.Message) + "\n"); }
        }
        internal static MethodInfo Resolve(Assembly root, string typeName, string method, int parameters, bool nested)
        {
            Type type = root.GetType(typeName, true)!;
            var types = nested ? type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic) : new[] { type };
            var methods = types.SelectMany(t => t.GetMethods(Flags)).Where(m => m.Name == method && m.GetParameters().Length == parameters && !m.ContainsGenericParameters && !m.IsAbstract).ToArray();
            if (methods.Length != 1) throw new InvalidOperationException("Expected one method, got " + methods.Length);
            return methods[0];
        }
        internal static void Start(MethodBase __originalMethod, out State __state)
        {
            __state = default;
            try {
                if (!Targets.TryGetValue(__originalMethod, out Info info)) return;
                Scope scope = current;
                if (info.RequiresScope && scope.Id == 0) return;
                Interlocked.Increment(ref info.Calls);
                __state = new State { Info = info, Started = Stopwatch.GetTimestamp(), Thread = Thread.CurrentThread.ManagedThreadId, Scope = scope };
            } catch { Interlocked.Increment(ref errors); }
        }
        internal static void StartScoped(MethodBase __originalMethod, out State __state)
        {
            // Global terrain methods are frequently used elsewhere; skip lookup and timing
            // unless this thread is inside a forest terrain-generation step.
            if (current.Id == 0) { __state = default; return; }
            Start(__originalMethod, out __state);
        }
        internal static void End(State __state, Exception? __exception)
        {
            try {
                var info = __state.Info; var writer = sink;
                if (info == null || writer == null) return;
                double ms = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
                if (ms < 25) return;
                Interlocked.Increment(ref info.Slow);
                int main = Volatile.Read(ref StepProbe.MainThreadId);
                writer.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), info.Stage, ms,
                    __state.Scope.Id == 0 ? (object)"" : __state.Scope.Id, __state.Scope.Stage ?? "", __state.Scope.Chunk ?? "",
                    __state.Thread, main == 0 ? "unknown" : (__state.Thread == main ? "true" : "false"),
                    __exception?.GetType().FullName ?? "", writer.Dropped));
            } catch { Interlocked.Increment(ref errors); }
        }
        internal static void WriteHealth()
        {
            var writer = health; var events = sink;
            if (writer == null || events == null) return;
            foreach (var info in Targets.Values)
                writer.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), info.Stage, Interlocked.Read(ref info.Calls), Interlocked.Read(ref info.Slow), Interlocked.Read(ref errors), events.Error ?? "", events.Dropped, writer.Dropped));
        }
        internal static void Dispose()
        {
            var a = sink; var b = health; sink = null; health = null;
            try { a?.Dispose(); } catch { } try { b?.Dispose(); } catch { }
        }
    }
}
