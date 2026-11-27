using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CavePerformance;
using HarmonyLib;

internal static class LoadTransitionTimingTests
{
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private sealed class NoPostContext : SynchronizationContext
    {
        internal int Posts;
        public override void Post(SendOrPostCallback callback, object state) { Posts++; throw new Exception("Timing must not post a Unity continuation"); }
    }
    private static Task<int> generic = null!;
    [MethodImpl(MethodImplOptions.NoInlining)] private static Task<int> NativeGeneric() => generic;
    private static void StartFixture(out LoadTransitionTiming.Call __state) => __state = new LoadTransitionTiming.Call(new LoadTransitionTiming.Load(91, "kind=fixture;layer=100"), "file_ready");
    private static void ReturnedFixture(Task __result, LoadTransitionTiming.Call __state) => LoadTransitionTiming.Observe(__result, __state);
    internal static void Run()
    {
        var previousEmit = LoadTransitionTiming.Emit;
        var previousNow = LoadTransitionTiming.Now;
        var previousContext = SynchronizationContext.Current;
        var events = new List<string>();
        var gate = new object();
        double now = 10;
        LoadTransitionTiming.Now = () => Volatile.Read(ref now);
        LoadTransitionTiming.Emit = (stage, ms, detail, error) => { lock (gate) events.Add(stage + ":" + ms + ":" + detail + ":" + error?.GetType().Name); };
        var harmony = new HarmonyLib.Harmony("ATT.CavePerformance.LoadTimingFixture");
        try
        {
            var load = new LoadTransitionTiming.Load(7, "kind=static;layer=20");
            var call = new LoadTransitionTiming.Call(load, "total");
            var original = new TaskCompletionSource<bool>();
            var context = new NoPostContext(); SynchronizationContext.SetSynchronizationContext(context);
            LoadTransitionTiming.Observe(original.Task, call);
            Check(events.Count == 0 && !original.Task.IsCompleted, "Observer completed or logged a pending native task");
            now = 35; original.SetResult(true);
            Check(original.Task.Result && events.Count == 1 && events[0].Contains("load_id=7") && events[0].Contains("status=completed") && events[0].Contains(":25:"), "Completed task did not retain correlated wall timing");
            Check(load.TotalFinished && context.Posts == 0, "Observer changed scheduling or missed load completion");
            LoadTransitionTiming.Finish(call, "failed", new Exception());
            Check(events.Count == 1, "One phase produced duplicate terminal events");

            var marker = new InvalidOperationException("original task failure");
            var fault = Task.FromException(marker);
            LoadTransitionTiming.Observe(fault, new LoadTransitionTiming.Call(load, "static_ready"));
            Exception? seen = null; try { fault.GetAwaiter().GetResult(); } catch (Exception error) { seen = error; }
            Check(ReferenceEquals(marker, seen) && events[1].Contains("status=failed") && events[1].Contains("InvalidOperationException"), "Task failure was changed or omitted");
            var cancelled = new TaskCompletionSource<bool>(); cancelled.SetCanceled();
            LoadTransitionTiming.Observe(cancelled.Task, new LoadTransitionTiming.Call(load, "generation_ready"));
            Check(cancelled.Task.IsCanceled && events[2].Contains("status=cancelled"), "Native cancellation was changed or unreported");

            var worker = new TaskCompletionSource<bool>();
            LoadTransitionTiming.Observe(worker.Task, new LoadTransitionTiming.Call(load, "file_ready"));
            Task.Run(() => worker.SetResult(true)).GetAwaiter().GetResult();
            lock (gate) Check(events.Count == 4 && events[3].Contains("phase=file_ready"), "Worker completion observation lost or posted to main context");
            Check(context.Posts == 0, "Worker task timing queued Unity work");

            var genericMethod = typeof(LoadTransitionTimingTests).GetMethod(nameof(NativeGeneric), BindingFlags.Static | BindingFlags.NonPublic)!;
            harmony.Patch(genericMethod,
                prefix: new HarmonyMethod(typeof(LoadTransitionTimingTests), nameof(StartFixture)),
                postfix: new HarmonyMethod(typeof(LoadTransitionTimingTests), nameof(ReturnedFixture)));
            var value = new TaskCompletionSource<int>(); generic = value.Task;
            var returned = NativeGeneric();
            Check(ReferenceEquals(returned, value.Task), "Generic native task identity changed");
            value.SetResult(17);
            Check(returned.Result == 17 && events.Count == 5 && events[4].Contains("load_id=91"), "Harmony observation of Task<T> lost native result or timing");

            LoadTransitionTiming.Emit = (stage, ms, detail, error) => throw new Exception("writer fixture");
            var healthy = Task.FromResult(23);
            LoadTransitionTiming.Observe(healthy, new LoadTransitionTiming.Call(load, "total"));
            Check(healthy.Result == 23, "A failed diagnostic changed the native task");
            LoadTransitionTiming.Bind();
            Check(typeof(Task).IsAssignableFrom(LoadTransitionTiming.Total.ReturnType) && typeof(Task).IsAssignableFrom(LoadTransitionTiming.File.ReturnType), "Installed load boundary resolution failed");
            LoadTransitionTiming.Attach(harmony);
            Check(HarmonyLib.Harmony.GetPatchInfo(LoadTransitionTiming.Total).Owners.Contains(harmony.Id), "Installed total-load timing hook missing");
            Console.WriteLine("PASS cave transition timing: native Task/Task<T> identity, failure/cancellation/result retained; no context posts; worker completion; exactly one terminal event; writer-fault isolation; installed boundary attachment.");
        }
        finally
        {
            harmony.UnpatchSelf(); LoadTransitionTiming.Ready = false;
            LoadTransitionTiming.Emit = previousEmit; LoadTransitionTiming.Now = previousNow;
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
}
