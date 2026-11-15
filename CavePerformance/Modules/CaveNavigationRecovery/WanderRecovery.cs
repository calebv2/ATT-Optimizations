using System;

namespace CaveNavigationRecovery
{
    internal sealed class WanderState
    {
        public WanderState() { }
        internal long Epoch;
        internal object[]? Arguments;
        internal int Retries;
        internal bool Pending;
        internal double Due;
        internal void Cancel(bool retry)
        {
            Epoch++; Pending = false; Arguments = null;
            if (!retry) Retries = 0;
        }
        internal bool Schedule(long epoch, object[]? arguments, double now, bool requesting)
        {
            if (Epoch != epoch || requesting || arguments == null || Pending || Retries >= 2) return false;
            Arguments = arguments; Pending = true; Due = now + 2;
            return true;
        }
    }
}
