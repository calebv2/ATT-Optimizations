using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace ForestStepDiagnostics
{
    public sealed class Core : ForestPerformance.Support.Module
    {
        private HarmonyLib.Harmony? harmony;
        private ServerTrace? sink, health;
        private double nextHealth; private bool warned;
        private string dir = "";
        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            try
            {
                dir = Path.Combine(DataDirectory, "Diagnostics", "ForestStepDiagnostics", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
                Directory.CreateDirectory(dir);
                sink = new ServerTrace(Path.Combine(dir, "slow-steps.csv"), "utc,stage,duration_ms,chunk_identifier,state_before,managed_thread_id,is_main_thread,exception_type,dropped_rows,context_error,scope_id");
                health = new ServerTrace(Path.Combine(dir, "health.csv"), "utc,stage,calls,slow_steps,context_errors,probe_errors,slow_writer_error,slow_dropped,health_dropped");
                FineProbe.Initialize(dir);
                StepProbe.Sink = sink;
                harmony = new HarmonyLib.Harmony("ATT.ForestPerformance.ForestStepDiagnostics");
                var root = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ForestChunk", false)).FirstOrDefault(t => t != null)?.Assembly
                    ?? throw new TypeLoadException("ForestChunk");
                string hookFile = Path.Combine(dir, "hooks.csv");
                File.WriteAllText(hookFile, "stage,status,detail\n");
                foreach (var spec in StepProbe.Specs)
                {
                    try
                    {
                        var info = StepProbe.Resolve(root, spec);
                        if (!StepProbe.Targets.TryAdd(info.Step, info)) throw new InvalidOperationException("duplicate hook");
                        try { harmony.Patch(info.Step, prefix: new HarmonyMethod(typeof(StepProbe).GetMethod(nameof(StepProbe.Start), BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(info.Step.DeclaringType!)), finalizer: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.End))); }
                        catch { StepProbe.Targets.TryRemove(info.Step, out _); throw; }
                        File.AppendAllText(hookFile, ServerTrace.Row(spec.Stage, "attached", info.Step.DeclaringType!.FullName) + "\n");
                    }
                    catch (Exception e) { File.AppendAllText(hookFile, ServerTrace.Row(spec.Stage, "unavailable", e.GetType().Name + ": " + e.Message) + "\n"); }
                }
                FineProbe.Install(root, harmony, hookFile);
                File.WriteAllText(Path.Combine(dir, "run.txt"), "Forest Step Diagnostics 0.2.0\nThreshold 25 ms inclusive wall time. Each async MoveNext invocation excludes await time and is not total task latency.\nFine timings are scoped to forest terrain steps or forest-specific sort workers. Scope IDs are thread-local and never propagate across awaits or worker threads.\nRows may overlap nested methods or other diagnostics. Chunk identifiers and state are captured before each step; chunk may be unavailable. Main thread is unknown until first OnUpdate. No Unity property is accessed for context.\n");
                LoggerInstance.Msg("Forest step diagnostics initialized; check hooks.csv: " + dir);
            }
            catch (Exception e) { LoggerInstance.Error("Forest step diagnostics unavailable: " + e); OnApplicationQuit(); }
        }
        public override void OnUpdate()
        {
            if (Volatile.Read(ref StepProbe.MainThreadId) == 0) Volatile.Write(ref StepProbe.MainThreadId, Thread.CurrentThread.ManagedThreadId);
            if (sink == null || health == null) return;
            if (!warned && (sink.Error != null || health.Error != null || FineProbe.Error != null)) { warned = true; LoggerInstance.Warning("Forest diagnostics writer error: " + (sink.Error ?? health.Error ?? FineProbe.Error)); }
            if (Time.realtimeSinceStartup < nextHealth) return;
            nextHealth = Time.realtimeSinceStartup + 60f;
            foreach (var info in StepProbe.Targets.Values)
                health.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), info.Stage, Interlocked.Read(ref info.Calls), Interlocked.Read(ref info.Slow),
                    Interlocked.Read(ref info.ContextErrors), Interlocked.Read(ref StepProbe.Errors), sink.Error ?? "", sink.Dropped, health.Dropped));
            FineProbe.WriteHealth();
        }
        public override void OnApplicationQuit() { StepProbe.Sink = null; try { harmony?.UnpatchSelf(); } catch { } FineProbe.Dispose(); try { sink?.Dispose(); } catch { } try { health?.Dispose(); } catch { } sink = null; health = null; }
    }

    internal static class StepProbe
    {
        internal sealed class Spec
        {
            internal readonly string Type, Method, Stage, ChunkKind; internal readonly int Parameters;
            internal Spec(string type, string method, int parameters, string stage, string chunkKind = "") { Type = type; Method = method; Parameters = parameters; Stage = stage; ChunkKind = chunkKind; }
        }
        internal static readonly Spec[] Specs = {
            new Spec("ForestChunk", "Alta.Chunks.IProceduralGenerationStreamer.LoadStatic", 1, "ForestChunk.LoadStatic", "owner"),
            new Spec("ForestChunk", "Alta.Chunks.IProceduralGenerationStreamer.LoadDynamic", 1, "ForestChunk.LoadDynamic", "owner"),
            new Spec("Alta.Forests.ForestGenerator", "RequestStatic", 3, "ForestGenerator.RequestStatic", "direct"),
            new Spec("Alta.Forests.ForestGenerator", "RequestDynamic", 1, "ForestGenerator.RequestDynamic", "direct"),
            new Spec("Alta.Forests.ForestGenerator", "GenerateTerrain", 1, "ForestGenerator.GenerateTerrain"),
            new Spec("Alta.Forests.ForestGenerator", "GenerateAsync", 0, "ForestGenerator.GenerateAsync"),
            new Spec("Alta.Forests.ForestGenerator", "GenerateInternalAsync", 1, "ForestGenerator.GenerateInternalAsync"),
            new Spec("Alta.Forests.ForestGeneration", "SortNodes", 0, "ForestGeneration.SortNodes"),
            new Spec("nested:Alta.Forests.ForestGenerator", "<GenerateTerrain>b__0", 0, "ForestGenerator.GenerateTerrain.Prepare"),
            new Spec("nested:Alta.Forests.ForestGenerator", "<GenerateTerrain>b__2", 0, "ForestGenerator.GenerateTerrain.Generate")
        };
        internal sealed class Info
        {
            internal long Calls, Slow, ContextErrors;
            internal readonly MethodInfo Step; internal readonly string Stage; internal readonly FieldInfo State; internal readonly FieldInfo? Chunk, Owner, OwnerChunk, ChunkId;
            internal Info(MethodInfo step, string stage, FieldInfo state, FieldInfo? chunk, FieldInfo? owner, FieldInfo? ownerChunk, FieldInfo? chunkId)
            { Step = step; Stage = stage; State = state; Chunk = chunk; Owner = owner; OwnerChunk = ownerChunk; ChunkId = chunkId; }
        }
        internal struct State { internal long Started, ScopeId; internal string? Chunk, Error; internal int Before, Thread; internal Info? Info; internal bool Scoped; internal FineProbe.Scope PreviousScope; }
        internal static readonly ConcurrentDictionary<MethodBase, Info> Targets = new ConcurrentDictionary<MethodBase, Info>();
        internal static ServerTrace? Sink; internal static int MainThreadId; internal static long Errors;
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static Info Resolve(Assembly root, Spec spec)
        {
            bool nested = spec.Type.StartsWith("nested:", StringComparison.Ordinal);
            Type t = root.GetType(nested ? spec.Type.Substring(7) : spec.Type, true)!;
            var candidateTypes = nested ? t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic) : new[] { t };
            MethodInfo[] methods = candidateTypes.SelectMany(c => c.GetMethods(Flags | BindingFlags.DeclaredOnly)).Where(m => m.Name == spec.Method && m.GetParameters().Length == spec.Parameters).ToArray();
            if (methods.Length != 1) throw new InvalidOperationException("Expected one matching method, got " + methods.Length);
            Type state = methods[0].GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType ?? throw new InvalidOperationException("No async state machine");
            MethodInfo step = state.GetMethod("MoveNext", Flags) ?? throw new MissingMethodException(state.FullName, "MoveNext");
            FieldInfo stateField = state.GetField("<>1__state", Flags) ?? throw new MissingFieldException(state.FullName, "<>1__state");
            FieldInfo? chunk = null, owner = null, ownerChunk = null, chunkId = null;
            if (spec.ChunkKind != "")
            {
                Type chunkType = root.GetType("Alta.Chunks.Chunk", true)!;
                chunkId = chunkType.GetField("chunkIdentifier", Flags) ?? throw new MissingFieldException(chunkType.FullName, "chunkIdentifier");
                if (spec.ChunkKind == "direct") chunk = state.GetField("chunk", Flags) ?? throw new MissingFieldException(state.FullName, "chunk");
                else { owner = state.GetField("<>4__this", Flags) ?? throw new MissingFieldException(state.FullName, "<>4__this"); ownerChunk = t.GetField("chunk", Flags) ?? throw new MissingFieldException(t.FullName, "chunk"); }
            }
            return new Info(step, spec.Stage, stateField, chunk, owner, ownerChunk, chunkId);
        }
        internal static void Start<T>(MethodBase __originalMethod, ref T __instance, out State __state) where T : struct
        {
            object boxed = __instance;
            __state = new State { Started = Stopwatch.GetTimestamp(), Before = int.MinValue, Thread = Thread.CurrentThread.ManagedThreadId };
            try
            {
                if (!Targets.TryGetValue(__originalMethod, out Info info)) return;
                Interlocked.Increment(ref info.Calls); __state.Info = info; __state.Before = (int)info.State.GetValue(boxed);
                object? chunk = info.Chunk?.GetValue(boxed);
                if (info.Owner != null) { object? owner = info.Owner.GetValue(boxed); if (owner != null) chunk = info.OwnerChunk!.GetValue(owner); }
                if (chunk != null) __state.Chunk = info.ChunkId?.GetValue(chunk) as string;
            }
            catch (Exception e) { __state.Error = e.GetType().Name; if (__state.Info != null) Interlocked.Increment(ref __state.Info.ContextErrors); else Interlocked.Increment(ref Errors); }
            try {
                if (__state.Info != null && (__state.Info.Stage == "ForestGenerator.GenerateTerrain" || __state.Info.Stage.StartsWith("ForestGenerator.GenerateTerrain.", StringComparison.Ordinal))) {
                    __state.PreviousScope = FineProbe.Enter(__state.Info.Stage, __state.Chunk, out __state.ScopeId); __state.Scoped = true;
                }
            } catch { Interlocked.Increment(ref Errors); }
        }
        internal static void End(State __state, Exception? __exception)
        {
            try
            {
                var sink = Sink; if (sink == null || __state.Info == null) return;
                double ms = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
                if (ms < 25) return;
                Interlocked.Increment(ref __state.Info.Slow);
                int main = Volatile.Read(ref MainThreadId);
                sink.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), __state.Info.Stage, ms, __state.Chunk ?? "", __state.Before == int.MinValue ? (object)"" : __state.Before,
                    __state.Thread, main == 0 ? "unknown" : (__state.Thread == main ? "true" : "false"), __exception?.GetType().FullName ?? "", sink.Dropped, __state.Error ?? "", __state.ScopeId == 0 ? (object)"" : __state.ScopeId));
            }
            catch { Interlocked.Increment(ref Errors); }
            finally { if (__state.Scoped) FineProbe.Exit(__state.PreviousScope); }
        }
    }
}
