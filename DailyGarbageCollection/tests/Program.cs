using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DailyGarbageCollection;
using HarmonyLib;
using Patch = DailyGarbageCollection.Patch;

internal sealed class FakeTimer
{
    public int Calls;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Start() { Calls++; }
}
internal sealed class FakeScan
{
    internal static readonly Exception Sentinel = new InvalidOperationException("sentinel");
    public int Calls;
    [MethodImpl(MethodImplOptions.NoInlining)] public bool MoveNext(bool fail) { Calls++; GC.Collect(); if (fail) throw Sentinel; return true; }
}
internal static class OtherOwner
{
    internal static int Begins, Ends;
    internal static void Prefix() { Begins++; }
    internal static void Finalizer() { Ends++; }
}
internal static class Program
{
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static HarmonyMethod HM(Type type, string name) => new HarmonyMethod(type, name);
    static void Main()
    {
        TimeZoneInfo zone = DailySchedule.ResolveEastern();
        DateTime winter = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2026, 11, 11, 11, 0, 0, DateTimeKind.Utc), zone);
        DateTime summer = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2027, 6, 1, 10, 0, 0, DateTimeKind.Utc), zone);
        Check(winter.Hour == 6 && summer.Hour == 6, "Eastern standard/daylight time mismatch");
        Check(!DailySchedule.ShouldRun(winter.AddSeconds(-1), null), "ran before six");
        Check(DailySchedule.ShouldRun(winter, null) && DailySchedule.ShouldRun(winter.AddMinutes(4), null), "missed grace window");
        Check(!DailySchedule.ShouldRun(winter.AddMinutes(5), null), "late startup incorrectly collected");
        Check(!DailySchedule.ShouldRun(winter, "2026-11-11"), "duplicate date collected");
        Check(!DailySchedule.ShouldRun(winter, "2026-11-12"), "clock rollback triggered an old-date collection");
        string dir = Path.Combine(Path.GetTempPath(), "daily-gc-smoke-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try
        {
            string marker = Path.Combine(dir, "marker.txt");
            Check(DailySchedule.ReadMarker(marker) == null, "new marker");
            DailySchedule.WriteMarker(marker, "2026-11-11"); Check(DailySchedule.ReadMarker(marker) == "2026-11-11", "first marker");
            DailySchedule.WriteMarker(marker, "2026-11-12"); Check(DailySchedule.ReadMarker(marker) == "2026-11-12", "replaced marker");
            File.WriteAllText(marker, "bad");
            try { DailySchedule.ReadMarker(marker); throw new Exception("bad marker accepted"); } catch (InvalidDataException) { }
        }
        finally { Directory.Delete(dir, true); }

        var patch = new HarmonyLib.Harmony("ATT.DailyGC.Fixture");
        var other = new HarmonyLib.Harmony("ATT.DailyGC.Other");
        var cave = new HarmonyLib.Harmony("ATT.DailyGC.RealCaveProbe");
        try
        {
            MethodInfo timer = typeof(FakeTimer).GetMethod(nameof(FakeTimer.Start))!;
            MethodInfo scan = typeof(FakeScan).GetMethod(nameof(FakeScan.MoveNext))!;
            other.Patch(scan, prefix: HM(typeof(OtherOwner), nameof(OtherOwner.Prefix)), finalizer: HM(typeof(OtherOwner), nameof(OtherOwner.Finalizer)));
            patch.Patch(timer, prefix: HM(typeof(Patch), nameof(Patch.SkipGameTimer)));
            patch.Patch(scan, transpiler: HM(typeof(Patch), nameof(Patch.ReplaceScanCollect)));
            var ft = new FakeTimer(); ft.Start(); Check(ft.Calls == 0, "timer still executed");
            var fs = new FakeScan(); long before = Patch.SuppressedCaveCollections;
            Check(fs.MoveNext(false) && fs.Calls == 1, "scan result/side effect changed");
            try { fs.MoveNext(true); throw new Exception("exception swallowed"); } catch (InvalidOperationException e) { Check(ReferenceEquals(e, FakeScan.Sentinel), "exception identity changed"); }
            Check(fs.Calls == 2 && Patch.SuppressedCaveCollections == before + 2, "target GC call not intercepted twice");
            Check(OtherOwner.Begins == 2 && OtherOwner.Ends == 2, "other Harmony owner changed");
            patch.UnpatchSelf();
            ft.Start(); Check(ft.Calls == 1, "timer not restored after unpatch");
            Check(Harmony.GetPatchInfo(scan).Owners.Contains(other.Id), "unpatch removed other owner");
            other.UnpatchSelf();

            string game = Environment.GetEnvironmentVariable("ATT_GAME_PATH")!;
            Assembly root = Assembly.LoadFrom(Path.Combine(game, "A Township Tale_Data/Managed/Root.Township.dll"));
            var targets = Patch.Resolve(root);
            string caveHooks = Path.Combine(Path.GetTempPath(), "daily-gc-cave-hooks-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                string caveDll = Environment.GetEnvironmentVariable("ATT_CAVE_PERFORMANCE_DLL")!;
                Assembly caveAssembly = Assembly.LoadFrom(caveDll);
                Type caveProbe = caveAssembly.GetType("CaveStepDiagnostics.PathProbe", true)!;
                caveProbe.GetMethod("AttachAll", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { root, cave, caveHooks });
                Check(File.ReadAllLines(caveHooks).Skip(1).All(x => x.Contains(",attached,")), "real cave probe failed to attach");
                Check(Harmony.GetPatchInfo(targets.scan).Owners.Contains(cave.Id), "real cave probe did not patch scan");
                patch.Patch(targets.scan, transpiler: HM(typeof(Patch), nameof(Patch.ReplaceScanCollect)));
                patch.Patch(targets.timer, prefix: HM(typeof(Patch), nameof(Patch.SkipGameTimer)));
                Check(Harmony.GetPatchInfo(targets.scan).Owners.Contains(cave.Id) && Harmony.GetPatchInfo(targets.scan).Owners.Contains(patch.Id), "real cave probe coexistence failed");
                Check(Harmony.GetPatchInfo(targets.timer).Owners.Contains(patch.Id), "actual timer attachment failed");
                patch.UnpatchSelf();
                Check(Harmony.GetPatchInfo(targets.scan).Owners.Contains(cave.Id), "actual scan unpatch removed cave probe");
            }
            finally { cave.UnpatchSelf(); File.Delete(caveHooks); }
            other.UnpatchSelf();
            Console.WriteLine("PASS: actual game timer and scan patch attach, Harmony owner preserved; original game methods not executed.");
            Console.WriteLine("PASS: targeted fake scan skips only its GC call, returns and exceptions preserved; timer restored on unpatch.");
            Console.WriteLine("PASS: Eastern standard/daylight time, 06:00 window, duplicate prevention and persisted marker.");
        }
        finally { patch.UnpatchSelf(); other.UnpatchSelf(); cave.UnpatchSelf(); }
    }
}
