// This component predates nullable annotations.
#nullable disable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ForestResetFix
{
    // Ordering shared by the real game adapter and failure-injection tests.
    public interface IResetSteps
    {
        void Validate();
        void EnterMaintenance();
        Task EnsureNoPlayers(bool disconnect);
        Task PrepareOldGeneration();
        Task QuiesceChunks();
        void HoldGeneration();
        Task ClearOldData();
        void SelectSeedAndInvalidate();
        void ReleaseGeneration();
        Task RebuildAndValidate();
        Task Persist();
        void ExitMaintenance();
    }

    public static class ResetFlow
    {
        public static async Task Run(IResetSteps steps, bool disconnect)
        {
            steps.Validate();
            steps.EnterMaintenance();
            try
            {
                await steps.EnsureNoPlayers(disconnect);
                // Native GenerateAsync waits on wipeLock: never set that lock first.
                await steps.PrepareOldGeneration();
                await steps.QuiesceChunks();
                steps.HoldGeneration();
                await steps.ClearOldData();
                steps.SelectSeedAndInvalidate();
                // New generation must be allowed to start before awaiting it.
                steps.ReleaseGeneration();
                await steps.RebuildAndValidate();
                await steps.Persist();
            }
            finally
            {
                try { steps.ReleaseGeneration(); }
                finally { steps.ExitMaintenance(); }
            }
        }
    }

    public static class CacheTasks
    {
        // The native registry holds the inner generator task. Its outer cache
        // wrapper queues the serialized write and only then removes this key.
        // Await both inner completion and successful native removal before the
        // caller flushes disk writes or commits a new seed.
        public static async Task WaitForCompletion(IDictionary<string, Task> tasks, IEnumerable<string> keys,
            Action resumed, int finalizationTimeoutMilliseconds = 30000)
        {
            if (finalizationTimeoutMilliseconds < 0) throw new ArgumentOutOfRangeException("finalizationTimeoutMilliseconds");
            var selected = new HashSet<string>(keys, StringComparer.Ordinal);
            var recorded = new HashSet<Task>();
            var failures = new List<Exception>();
            Stopwatch finalizing = null;
            resumed();
            while (true)
            {
                var snapshot = new List<KeyValuePair<string, Task>>();
                var pending = new List<Task>();
                var completed = new List<KeyValuePair<string, Task>>();
                foreach (string key in selected)
                {
                    Task task;
                    if (!tasks.TryGetValue(key, out task)) continue;
                    snapshot.Add(new KeyValuePair<string, Task>(key, task));
                    if (!task.IsCompleted) pending.Add(task);
                    else if (!task.IsFaulted && !task.IsCanceled) completed.Add(new KeyValuePair<string, Task>(key, task));
                }
                RecordFailures(snapshot, recorded, failures);
                if (pending.Count != 0)
                {
                    // WhenAll waits for every sibling even when one faults.
                    // No timeout cancels live native work or releases its lock.
                    try { await Task.WhenAll(pending); }
                    catch { /* Preserve each key and original exception below. */ }
                    resumed();
                    RecordFailures(snapshot, recorded, failures);
                    finalizing = null;
                    continue;
                }
                if (completed.Count == 0)
                {
                    ThrowFailures(failures);
                    return;
                }
                if (finalizing == null) finalizing = Stopwatch.StartNew();
                if (finalizing.ElapsedMilliseconds >= finalizationTimeoutMilliseconds)
                {
                    foreach (var pair in completed)
                        failures.Add(new InvalidOperationException("Forest cache task RanToCompletion but native cache finalization did not finish: " + pair.Key));
                    ThrowFailures(failures);
                }
                // Let the native outer continuations serialize, queue writes,
                // and unregister their completed inner tasks on the game thread.
                await Task.Delay(10);
                resumed();
            }
        }

        private static void RecordFailures(IEnumerable<KeyValuePair<string, Task>> snapshot,
            HashSet<Task> recorded, List<Exception> failures)
        {
            foreach (var pair in snapshot)
                if ((pair.Value.IsFaulted || pair.Value.IsCanceled) && recorded.Add(pair.Value))
                    failures.Add(Failure(pair.Key, pair.Value));
        }

        public static InvalidOperationException Failure(string key, Task task)
        {
            Exception cause = task.IsFaulted ? (Exception)task.Exception.Flatten()
                : task.IsCanceled ? new TaskCanceledException(task) : null;
            return new InvalidOperationException("Forest cache task " + task.Status + ": " + key
                + (cause == null ? "" : " -- " + cause.Message), cause);
        }

        private static void ThrowFailures(List<Exception> failures)
        {
            if (failures.Count == 1) throw failures[0];
            if (failures.Count > 1)
                throw new AggregateException("Forest cache tasks failed: " + string.Join("; ", failures.ConvertAll(e => e.Message).ToArray()), failures);
        }

        public static bool Owned(string key, string forest)
        {
            // CacheManager.GetCacheName appends version/chunk/seed information.
            return key.EndsWith(".cache", StringComparison.Ordinal)
                && (key.IndexOf(" - Forest Nodes " + forest + " - ", StringComparison.Ordinal) >= 0
                || key.IndexOf(" - Forest Terrain - " + forest + " - ", StringComparison.Ordinal) >= 0
                || key.IndexOf(" - Forest Static - " + forest + " - ", StringComparison.Ordinal) >= 0
                || key.IndexOf(" - River Nodes - " + forest + " - ", StringComparison.Ordinal) >= 0);
        }

        public static int RemoveFailed(IDictionary<string, Task> tasks, string forest)
        {
            var remove = new List<string>();
            foreach (var pair in tasks)
                if (Owned(pair.Key, forest) && (pair.Value.IsFaulted || pair.Value.IsCanceled))
                    remove.Add(pair.Key);
            foreach (string key in remove) tasks.Remove(key);
            return remove.Count;
        }

        public static void EnsureSettled(IDictionary<string, Task> tasks, string forest)
        {
            foreach (var pair in tasks)
                if (Owned(pair.Key, forest) && !pair.Value.IsCompleted)
                    throw new InvalidOperationException("Forest cache work is still running: " + pair.Key);
        }
    }

    // The task is published before invoking callbacks, including synchronous callbacks.
    public sealed class OperationGate
    {
        private object owner;
        private int seed;
        private bool disconnect;
        private TaskCompletionSource<bool> completion;
        public bool IsBusy { get { return completion != null; } }

        public Task Start(object requestedOwner, int requestedSeed, bool requestedDisconnect, Func<Task> operation)
        {
            if (completion != null)
            {
                if (ReferenceEquals(owner, requestedOwner) && seed == requestedSeed && disconnect == requestedDisconnect)
                    return completion.Task;
                return Failed(new InvalidOperationException("A forest reset is already running."));
            }
            owner = requestedOwner; seed = requestedSeed; disconnect = requestedDisconnect;
            var current = new TaskCompletionSource<bool>(); completion = current;
            Complete(operation, current);
            return current.Task;
        }

        private async void Complete(Func<Task> operation, TaskCompletionSource<bool> current)
        {
            Exception failure = null;
            try { await operation(); }
            catch (Exception error) { failure = error; }
            // Await continuations may execute inline: release before notifying them.
            if (ReferenceEquals(completion, current)) { completion = null; owner = null; }
            if (failure == null) current.TrySetResult(true); else current.TrySetException(failure);
        }

        private static Task Failed(Exception error)
        {
            var result = new TaskCompletionSource<bool>(); result.SetException(error); return result.Task;
        }
    }
}
