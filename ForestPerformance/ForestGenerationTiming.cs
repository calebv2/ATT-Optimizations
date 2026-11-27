using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Alta.Chunks;
using Alta.Forests;
using Alta.Static;
using HarmonyLib;

namespace ForestPerformance
{
    internal static class ForestGenerationTiming
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private const int GateSampleLimit = 1024;
        private static int activeGateSamples;
        private static long omittedGateSamples;

        internal static bool BridgePreparationAttached { get; private set; }

        private struct BridgeSample
        {
            internal long Started;
            internal string? Chunk;
            internal int Gen0, Gen1, Gen2;
        }

        internal static bool Attach(HarmonyLib.Harmony harmony)
        {
            BridgePreparationAttached = false;
            var bridge = typeof(ForestBridges).GetMethod("Alta.Forests.IForestGeneration.GenerateStatic", PrivateInstance,
                null, new[] { typeof(ForestGeneration), typeof(LocationChunk), typeof(InstructionSet) }, null);
            if (bridge == null || bridge.ReturnType != typeof(Task)) throw new MissingMethodException("ForestBridges.GenerateStatic");
            harmony.Patch(bridge, prefix: Own(nameof(BeginBridge)), finalizer: Own(nameof(EndBridge)));

            var rewrite = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "ForestRewrite.Server");
            if (rewrite == null) return false;
            Guid buildId = rewrite.ManifestModule.ModuleVersionId;
            if (buildId != RewriteTerrain.Rewrite053 && buildId != RewriteTerrain.Rewrite054) return false;
            Type type = rewrite.GetType("ForestRewrite.ForestRewriteMod", true)!;
            MethodInfo? build = type.GetMethod("GenerateChunkInstructions", PrivateStatic);
            Type? state = build?.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
            MethodInfo? step = state?.GetMethod("MoveNext", PrivateInstance);
            if (step == null) throw new MissingMethodException("ForestRewrite.GenerateChunkInstructions.MoveNext");
            harmony.Patch(step, transpiler: Own(nameof(AttachGateObserver)));
            BridgePreparationAttached = buildId == RewriteTerrain.Rewrite054 && BridgeIntersectionPreparation.Supported;
            return true;
        }

        internal static void Reset() => BridgePreparationAttached = false;

        private static HarmonyMethod Own(string name) => new HarmonyMethod(typeof(ForestGenerationTiming).GetMethod(name, PrivateStatic));

        private static void BeginBridge(LocationChunk __1, out BridgeSample __state)
        {
            __state = default;
            if (!Core.Ready || !Core.Main || Core.Trace == null) return;
            try
            {
                __state = new BridgeSample { Chunk = __1?.ChunkIdentifier, Gen0 = GC.CollectionCount(0),
                    Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2), Started = Stopwatch.GetTimestamp() };
            }
            catch { }
        }

        private static void EndBridge(BridgeSample __state, Exception? __exception)
        {
            if (__state.Started == 0) return;
            double ms = Elapsed(__state.Started);
            if (ms >= 25 || __exception != null)
                Core.Row("forest_bridge_generate", ms, "chunk=" + __state.Chunk + ";unit=synchronous_wall_time;gc0_delta=" +
                    (GC.CollectionCount(0) - __state.Gen0) + ";gc1_delta=" + (GC.CollectionCount(1) - __state.Gen1) +
                    ";gc2_delta=" + (GC.CollectionCount(2) - __state.Gen2), __exception);
        }

        private static IEnumerable<CodeInstruction> AttachGateObserver(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            FieldInfo? chunk = __originalMethod.DeclaringType?.GetField("chunk", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (chunk?.FieldType != typeof(LocationChunk)) throw new MissingFieldException("ForestRewrite state machine chunk");

            if (__originalMethod.Module.ModuleVersionId == RewriteTerrain.Rewrite054)
                AttachBridgePreparation(code, __originalMethod);

            var wait = typeof(SemaphoreSlim).GetMethod(nameof(SemaphoreSlim.WaitAsync), new[] { typeof(CancellationToken) });
            int[] calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(wait)).ToArray();
            if (calls.Length != 1) throw new InvalidOperationException("Expected one cancellable forest generation gate");
            code.InsertRange(calls[0] + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldfld, chunk),
                new CodeInstruction(OpCodes.Call, typeof(ForestGenerationTiming).GetMethod(nameof(ObserveGate), PrivateStatic)) });
            return code;
        }

        private static void AttachBridgePreparation(List<CodeInstruction> code, MethodBase originalMethod)
        {
            var generation = typeof(IForestGeneration).GetMethod(nameof(IForestGeneration.GenerateStatic),
                new[] { typeof(ForestGeneration), typeof(LocationChunk), typeof(InstructionSet) });
            int[] calls = Enumerable.Range(0, code.Count).Where(i => code[i].Calls(generation)).ToArray();
            var cancellation = originalMethod.DeclaringType?.GetField("<cancellation>5__3", PrivateInstance);
            if (calls.Length != 1 || cancellation?.FieldType != typeof(CancellationToken))
                throw new InvalidOperationException("Unexpected ForestRewrite bridge generation call shape");

            // Keep the existing labels and exception blocks on the first replacement instruction.
            int call = calls[0];
            code[call].opcode = OpCodes.Ldarg_0;
            code[call].operand = null;
            code.InsertRange(call + 1, new[] { new CodeInstruction(OpCodes.Ldfld, cancellation),
                new CodeInstruction(OpCodes.Call, typeof(BridgeIntersectionPreparation).GetMethod(nameof(BridgeIntersectionPreparation.Generate), PrivateStatic)) });
        }

        private static Task ObserveGate(Task original, LocationChunk chunk)
        {
            if (!Core.Ready || Core.Trace == null || original.IsCompleted) return original;
            if (Interlocked.Increment(ref activeGateSamples) > GateSampleLimit)
            {
                Interlocked.Decrement(ref activeGateSamples);
                Interlocked.Increment(ref omittedGateSamples);
                return original;
            }
            try
            {
                string label = chunk.ChunkIdentifier;
                long started = Stopwatch.GetTimestamp();
                _ = original.ContinueWith(finished =>
                {
                    Interlocked.Decrement(ref activeGateSamples);
                    double ms = Elapsed(started);
                    if (ms >= 25)
                        Core.Row("forest_cold_generation_gate", ms, "chunk=" + label + ";status=" + finished.Status +
                            ";unit=elapsed_wait;omitted=" + Interlocked.Read(ref omittedGateSamples));
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch
            {
                Interlocked.Decrement(ref activeGateSamples);
                Interlocked.Increment(ref omittedGateSamples);
            }
            return original;
        }

        private static double Elapsed(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
    }
}
