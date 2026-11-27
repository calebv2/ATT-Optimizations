using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CaveNavigationRecovery;
using HarmonyLib;

internal static partial class Tests
{
    private static Task? suppliedStaticTask;
    private static bool StaticLoadFixtureBody(ref Task __result) { __result = suppliedStaticTask!; return false; }
    private static void StaticHookTests(HarmonyLib.Harmony recovery, Assembly root)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var ownerThread = typeof(Core).GetField("ownerThread", flags)!;
        object? previousThread = ownerThread.GetValue(null);
        var nativeFixture = new HarmonyLib.Harmony("ATT.CaveNavigationRecovery.StaticNativeFixture");
        try
        {
            ownerThread.SetValue(null, Thread.CurrentThread.ManagedThreadId);
            nativeFixture.Patch(Core.StaticLoad, prefix: new HarmonyMethod(typeof(Tests), nameof(StaticLoadFixtureBody)) { priority = Priority.Last });
            Type cave = root.GetType("Alta.Chunks.StaticCaveChunk", true)!;
            FieldInfo completionField = cave.GetField("pathfindingGenerated", BindingFlags.Instance | BindingFlags.NonPublic)!;
            MethodInfo stateFor = typeof(Core).GetMethod("StateFor", flags)!;
            foreach (string outcome in new[] { "success", "failure", "cancel" })
            {
                object owner = FormatterServices.GetUninitializedObject(cave);
                var completion = new TaskCompletionSource<bool>(); completionField.SetValue(owner, completion);
                var state = (Core.StaticState)stateFor.Invoke(null, new[] { owner })!;
                using (var source = new CancellationTokenSource())
                {
                    suppliedStaticTask = completion.Task;
                    var returned = (Task)Core.StaticLoad.Invoke(owner, new object[] { source.Token })!;
                    Check(ReferenceEquals(returned, completion.Task), "static load Harmony hooks replaced the native task: " + outcome);
                    Check(!returned.IsCompleted, "static load hook fabricated completion");
                    if (outcome == "success")
                    {
                        state.Graph = new object(); state.Scan.CompletedGraph = state.Graph; state.Settle(null, false);
                        Check(returned.Status == TaskStatus.RanToCompletion, "native static success lost");
                    }
                    else if (outcome == "failure")
                    {
                        var sentinel = new InvalidOperationException("owned scan terminal failure"); state.Settle(sentinel, false);
                        Check(returned.IsFaulted && ReferenceEquals(returned.Exception!.InnerException, sentinel), "native static exception changed");
                    }
                    else
                    {
                        source.Cancel();
                        Check(state.CancellationRequested && !returned.IsCompleted, "cancellation bypassed safe native scan completion");
                        state.Settle(null, true);
                        Check(returned.IsCanceled, "native static cancellation not settled");
                    }
                }
            }
            object pendingOwner = FormatterServices.GetUninitializedObject(cave);
            completionField.SetValue(pendingOwner, new TaskCompletionSource<bool>());
            object iterator = Core.StaticGenerate.Invoke(pendingOwner, null)!;
            Check(iterator is ScanRecovery && iterator is IEnumerator, "actual StaticCaveChunk.GeneratePathfinding factory did not acquire our recovery wrapper");
            StaticShutdownReentry(cave, completionField, stateFor);
            // The actual factory is exercised; scene/graph generation is covered
            // by the host fixtures because Unity native calls require a player.
            Console.WriteLine("PASS actual installed static scan factory prefix and static load hooks: native task identity, original success/error, deferred cancellation and same completion source preserved.");
        }
        finally
        {
            nativeFixture.UnpatchSelf(); ownerThread.SetValue(null, previousThread); suppliedStaticTask = null;
            typeof(Core).GetMethod("ShutdownStatic", flags)!.Invoke(null, null);
        }
    }
    private static void StaticShutdownReentry(Type cave, FieldInfo completionField, MethodInfo stateFor)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var pending = (HashSet<Core.StaticState>)typeof(Core).GetField("PendingStatic", flags)!.GetValue(null)!;
        var first = new TaskCompletionSource<bool>(); var second = new TaskCompletionSource<bool>(); var added = new TaskCompletionSource<bool>();
        object firstOwner = FormatterServices.GetUninitializedObject(cave), secondOwner = FormatterServices.GetUninitializedObject(cave), addedOwner = FormatterServices.GetUninitializedObject(cave);
        completionField.SetValue(firstOwner, first); completionField.SetValue(secondOwner, second); completionField.SetValue(addedOwner, added);
        var firstState = (Core.StaticState)stateFor.Invoke(null, new[] { firstOwner })!;
        var secondState = (Core.StaticState)stateFor.Invoke(null, new[] { secondOwner })!;
        var callback = first.Task.ContinueWith(_ =>
        {
            pending.Remove(secondState);
            stateFor.Invoke(null, new[] { addedOwner });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        typeof(Core).GetMethod("ShutdownStatic", flags)!.Invoke(null, null);
        Check(callback.Status == TaskStatus.RanToCompletion && first.Task.IsCanceled && second.Task.IsCanceled && added.Task.IsCanceled && pending.Count == 0,
            "reentrant static task completion changed the shutdown collection or stranded a new awaiter");
        var afterStop = (IEnumerator)Core.StaticGenerate.Invoke(addedOwner, null)!;
        Check(!(afterStop is ScanRecovery) && !afterStop.MoveNext(), "shutdown admitted another static scan");
        object procedural = FormatterServices.GetUninitializedObject(Core.Generate.DeclaringType!);
        afterStop = (IEnumerator)Core.Generate.Invoke(procedural, null)!;
        Check(!(afterStop is ScanRecovery) && !afterStop.MoveNext(), "shutdown admitted another procedural scan");
        Console.WriteLine("PASS static shutdown snapshots pending owners; synchronous load continuations may remove/add owners without stranding completion tasks.");
    }
}
