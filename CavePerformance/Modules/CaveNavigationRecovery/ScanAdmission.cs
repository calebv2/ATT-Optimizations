using System;
using System.Collections.Generic;

namespace CaveNavigationRecovery
{
    // Main-thread admission, not an A* graph lock. Both cave hosts retain one
    // reservation through graph creation, worker warmup and native completion.
    internal sealed class ScanAdmission
    {
        internal const int Capacity = 128;
        private readonly List<ScanRecovery> waiting = new List<ScanRecovery>();
        internal ScanRecovery? Owner { get; private set; }
        internal int Waiting => waiting.Count;
        internal bool TryEnter(ScanRecovery request, bool nativeScanning)
        {
            if (ReferenceEquals(Owner, request)) return true;
            if (!waiting.Contains(request))
            {
                if (waiting.Count >= Capacity) throw new InvalidOperationException("Cave scan admission queue reached its bounded capacity");
                waiting.Add(request);
            }
            if (Owner != null || nativeScanning || !ReferenceEquals(waiting[0], request)) return false;
            waiting.RemoveAt(0); Owner = request; return true;
        }
        internal void Release(ScanRecovery request)
        {
            waiting.Remove(request);
            if (ReferenceEquals(Owner, request)) Owner = null;
        }
    }
}
