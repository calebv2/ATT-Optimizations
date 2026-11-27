using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ForestPerformance
{
    // Delays whole native MoveNext calls. The existing native batch cap and
    // null-result accounting still own instruction order, results and yields.
    internal static class ForestGenerationBudget
    {
        internal static readonly Scheduler Shared = new Scheduler(() => Time.frameCount, () => Core.Now,
            () => Core.Config.GenerationBudgetMs, () => Core.Ready && Core.Main && Core.Config.InstructionsPerFrame > 0);
        internal static IEnumerator Wrap(IEnumerator native) => Shared.Wrap(native);
        internal static void Shutdown() => Shared.Clear();
        internal static string Health() => Shared.Health();

        internal sealed class Scheduler
        {
            private const int MaximumWaiters = 256;
            private readonly Func<int> frame;
            private readonly Func<double> now, allowance;
            private readonly Func<bool> active;
            private readonly int ownerThread = Thread.CurrentThread.ManagedThreadId;
            private readonly LinkedList<Iterator> waiting = new LinkedList<Iterator>();
            private int currentFrame;
            private bool hasFrame;
            private double spent;
            private int stepsInFrame;
            internal long Steps, Deferrals, BudgetDeferrals, FairnessDeferrals, StaleTickets, Overflow;
            internal double TotalMs, MaximumStepMs, MaximumFrameMs, TotalWaitMs, MaximumWaitMs;
            internal int Pending => waiting.Count;
            internal Scheduler(Func<int> frame, Func<double> now, Func<double> allowance, Func<bool> active)
            { this.frame = frame; this.now = now; this.allowance = allowance; this.active = active; }
            internal IEnumerator Wrap(IEnumerator native) => new Iterator(this, native);
            private bool OnThread => Thread.CurrentThread.ManagedThreadId == ownerThread;
            private void Remove(Iterator item)
            {
                if (!OnThread) return; // Deferred cleanup is performed by Enter.
                if (item.Ticket == null) return;
                waiting.Remove(item.Ticket); item.Ticket = null;
            }
            private bool Enter(Iterator item, int at, double limit)
            {
                if (!hasFrame || currentFrame != at)
                { currentFrame = at; hasFrame = true; spent = 0; stepsInFrame = 0; }
                item.LastSeen = at;
                // A stopped coroutine is not guaranteed to Dispose its iterator.
                // Only deferred calls have tickets (their Current is null), so
                // a ticket not polled for two frames cannot hold up live loads.
                while (waiting.First != null && (waiting.First.Value.ended || unchecked(at - waiting.First.Value.LastSeen) > 1))
                { Remove(waiting.First.Value); StaleTickets++; }
                if (item.Ticket == null)
                {
                    if (waiting.Count >= MaximumWaiters) { Overflow++; return true; }
                    item.Ticket = waiting.AddLast(item);
                }
                bool budget = stepsInFrame > 0 && spent >= limit;
                bool turn = !ReferenceEquals(waiting.First!.Value, item);
                if (budget || turn)
                {
                    Deferrals++;
                    if (budget) BudgetDeferrals++;
                    if (turn) FairnessDeferrals++;
                    if (!item.Waiting) { item.Waiting = true; item.WaitStarted = now(); }
                    return false;
                }
                Remove(item);
                if (item.Waiting)
                {
                    double waited = Math.Max(0, now() - item.WaitStarted);
                    TotalWaitMs += waited; MaximumWaitMs = Math.Max(MaximumWaitMs, waited); item.Waiting = false;
                }
                return true;
            }
            private void Charge(double elapsed)
            {
                elapsed = Math.Max(0, elapsed);
                spent += elapsed; stepsInFrame++; Steps++; TotalMs += elapsed;
                MaximumStepMs = Math.Max(MaximumStepMs, elapsed); MaximumFrameMs = Math.Max(MaximumFrameMs, spent);
            }
            internal void Clear()
            {
                if (!OnThread) return;
                foreach (var item in waiting) { item.Ticket = null; item.Waiting = false; }
                waiting.Clear(); hasFrame = false; spent = 0; stepsInFrame = 0;
            }
            internal string Health() => "forest_budget_ms=" + Core.F(allowance()) + ";forest_budget_steps=" + Steps +
                ";forest_budget_deferred=" + Deferrals + ";forest_budget_time_deferred=" + BudgetDeferrals +
                ";forest_budget_turn_deferred=" + FairnessDeferrals + ";forest_budget_waiters=" + Pending +
                ";forest_budget_stale=" + StaleTickets + ";forest_budget_overflow=" + Overflow +
                ";forest_budget_work_ms=" + Core.F(TotalMs) + ";forest_budget_max_step_ms=" + Core.F(MaximumStepMs) +
                ";forest_budget_max_frame_ms=" + Core.F(MaximumFrameMs) + ";forest_budget_wait_ms=" + Core.F(TotalWaitMs) +
                ";forest_budget_max_wait_ms=" + Core.F(MaximumWaitMs);

            private sealed class Iterator : IEnumerator, IDisposable
            {
                private readonly Scheduler owner;
                private readonly IEnumerator native;
                internal volatile bool ended;
                private bool disposed;
                internal LinkedListNode<Iterator>? Ticket;
                internal int LastSeen;
                internal double WaitStarted;
                internal bool Waiting;
                public object? Current { get; private set; }
                internal Iterator(Scheduler owner, IEnumerator native) { this.owner = owner; this.native = native; }
                public bool MoveNext()
                {
                    if (ended) return false;
                    double limit = owner.allowance();
                    bool paced = owner.OnThread && owner.active() && limit > 0;
                    if (paced && !owner.Enter(this, owner.frame(), limit)) { Current = null; return true; }
                    if (!paced) { owner.Remove(this); Waiting = false; }
                    double started = paced ? owner.now() : 0;
                    try
                    {
                        bool more = native.MoveNext();
                        if (more) Current = native.Current;
                        else { ended = true; Current = null; owner.Remove(this); }
                        return more;
                    }
                    catch
                    {
                        ended = true; owner.Remove(this);
                        // Native exceptions retain their identity even if an
                        // iterator's cleanup itself fails.
                        try { Dispose(); } catch { }
                        throw;
                    }
                    finally { if (paced) owner.Charge(owner.now() - started); }
                }
                public void Dispose()
                {
                    if (disposed) return;
                    disposed = ended = true; Current = null; owner.Remove(this); Waiting = false;
                    (native as IDisposable)?.Dispose();
                }
                public void Reset() => throw new NotSupportedException();
            }
        }
    }
}
