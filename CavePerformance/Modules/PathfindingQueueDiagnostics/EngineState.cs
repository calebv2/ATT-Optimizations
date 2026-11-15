using System;
using System.Collections;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PathfindingQueueDiagnostics
{
    internal static class EngineState
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static FieldInfo processor = null!, queue = null!, locks = null!, threads = null!, coroutine = null!, head = null!, blocked = null!, terminating = null!, receivers = null!, blockedReceivers = null!, count = null!;
        private static FieldInfo graphUpdates = null!, graphThread = null!, work = null!, workQueue = null!, workLength = null!, returnQueue = null!, returnPop = null!;
        private static readonly string[] graphQueueNames = { "graphUpdateQueue", "graphUpdateQueueAsync", "graphUpdateQueueRegular", "graphUpdateQueuePost" };
        private static readonly FieldInfo[] graphQueues = new FieldInfo[4];
        internal static FieldInfo Field(Type type, string name) => type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name);
        internal static void Bind()
        {
            var astar = typeof(AstarPath);
            processor = Field(astar, "pathProcessor"); queue = Field(processor.FieldType, "queue");
            locks = Field(processor.FieldType, "locks"); threads = Field(processor.FieldType, "threads"); coroutine = Field(processor.FieldType, "threadCoroutine");
            var q = queue.FieldType;
            head = Field(q, "head"); blocked = Field(q, "blocked"); terminating = Field(q, "terminate");
            receivers = Field(q, "numReceivers"); blockedReceivers = Field(q, "blockedReceivers"); count = Field(q, "count");
            graphUpdates = Field(astar, "graphUpdates"); graphThread = Field(graphUpdates.FieldType, "graphUpdateThread");
            for (int i = 0; i < graphQueues.Length; i++) graphQueues[i] = Field(graphUpdates.FieldType, graphQueueNames[i]);
            work = Field(astar, "workItems"); workQueue = Field(work.FieldType, "workItems"); workLength = Field(workQueue.FieldType, "length");
            returnQueue = Field(astar, "pathReturnQueue"); returnPop = Field(returnQueue.FieldType, "pathReturnPop");
        }
        internal static string Read()
        {
            double start = Core.Now;
            var active = AstarPath.active;
            if (ReferenceEquals(active, null)) return "astar=unavailable";
            var p = processor.GetValue(active); if (p == null) return "processor=unavailable";
            var q = queue.GetValue(p)!;
            var b = new StringBuilder();
            b.Append("scanning=").Append(active.isScanning)
                .Append(";queue_head_present=").Append(head.GetValue(q) != null)
                .Append(";queue_count_hint=").Append(count.GetValue(q))
                .Append(";queue_blocked=").Append(blocked.GetValue(q))
                .Append(";terminating=").Append(terminating.GetValue(q))
                .Append(";receivers=").Append(receivers.GetValue(q))
                .Append(";blocked_receivers=").Append(blockedReceivers.GetValue(q))
                .Append(";locks=").Append((locks.GetValue(p) as ICollection)?.Count ?? -1)
                .Append(";coroutine_present=").Append(coroutine.GetValue(p) != null);
            var workers = threads.GetValue(p) as Thread[];
            b.Append(";worker_count=").Append(workers?.Length ?? 0);
            if (workers != null)
                for (int i = 0; i < Math.Min(16, workers.Length); i++)
                    b.Append(";worker_").Append(i).Append('=').Append(workers[i] == null ? "null" : workers[i].IsAlive + "/" + workers[i].ThreadState);
            var w = work.GetValue(active);
            b.Append(";work_items=").Append(w == null ? -1 : workLength.GetValue(workQueue.GetValue(w)));
            var g = graphUpdates.GetValue(active);
            if (g != null)
            {
                var gt = graphThread.GetValue(g) as Thread;
                b.Append(";graph_thread=").Append(gt == null ? "null" : gt.IsAlive + "/" + gt.ThreadState);
                for (int i = 0; i < graphQueues.Length; i++)
                    b.Append(';').Append(graphQueueNames[i]).Append('=').Append((graphQueues[i].GetValue(g) as ICollection)?.Count ?? -1);
            }
            var r = returnQueue.GetValue(active);
            b.Append(";return_pop_present=").Append(r != null && returnPop.GetValue(r) != null)
                .Append(";read_ms=").Append(Core.F(Core.Now - start));
            return b.ToString();
        }
        internal static void Emit(string context, long request, long span)
        {
            if (!Core.Main) return;
            Core.Row(Core.EngineRows, DateTime.UtcNow.ToString("O"), Core.F(Core.Now), context, request, span, Read(), Core.EngineRows?.Dropped ?? 0);
        }
    }
}
