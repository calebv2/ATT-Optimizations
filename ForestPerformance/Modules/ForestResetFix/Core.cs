// This component predates nullable annotations.
#nullable disable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Alta.Console;
using Alta.Chunks;
using Alta.Forests;
using Alta.Networking;
using Alta.Networking.Servers;
using Alta.Static;
using Alta.Utilities;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace ForestResetFix
{
    public sealed class Core : ForestPerformance.Support.Module
    {
        internal static readonly Guid SupportedGame = new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff");
        internal static Core Instance;
        internal static bool Ready;
        internal static int OwnerThread;
        internal static readonly OperationGate Gate = new OperationGate();
        internal static FieldInfo ForestSeed, ForestGenerationTask, ForestLock, Instructions, Terrain,
            RiverTask, RiverIntersections, RiverValues, RiverDepths, RiverEdges, RiverSegments, CacheGenerating, TerrainModified;
        private static HarmonyLib.Harmony harmony;
        private static Job currentJob;
        internal static bool MaintenanceHeld;

        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            Instance = this; OwnerThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                Bind();
                HookBodies.Reset = (obj, seed) => NetworkSceneManager.IsServer
                    ? Start((ForestGenerator)obj, seed, false, null) : InvokeOriginalUnavailable();
                RiverArgument<ForestGeneration, TerrainAccess>.Prepare = BeforeRiver;
                HookBodies.Clean = BeforeClean;
                harmony = new HarmonyLib.Harmony("ATT.ForestPerformance.ForestResetFix");
                Attach(harmony);
                object collection = typeof(CommandService).GetProperty("CommandCollection", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null, null);
                MethodInfo register = collection.GetType().GetMethods().Single(x => x.Name == "ProcessModule" && x.GetParameters().Length == 3);
                register.Invoke(collection, new object[] { typeof(Commands), typeof(Commands).Assembly, null });
                Ready = true;
                Log("READY v0.1.1; server-only; status is read-only; no automatic forest wipe.");
            }
            catch (Exception error)
            {
                if (harmony != null) harmony.UnpatchSelf();
                HookBodies.Reset = null; RiverArgument<ForestGeneration, TerrainAccess>.Prepare = null; HookBodies.Clean = null;
                LoggerInstance.Error("FOREST_RESET_FIX DISABLED " + error);
            }
        }

        private static Task InvokeOriginalUnavailable() { throw new InvalidOperationException("Forest reset is server-only."); }
        private static HarmonyMethod Hook(string name) { return new HarmonyMethod(typeof(HookBodies).GetMethod(name)) { priority = HarmonyLib.Priority.Last }; }
        private static void Attach(HarmonyLib.Harmony patcher)
        {
            patcher.Patch(typeof(ForestGenerator).GetMethod("Wipe"), prefix: Hook("WipePrefix"));
            patcher.Patch(typeof(RiverGenerator).GetMethod("GetGenerationAsync"), prefix: new HarmonyMethod(typeof(RiverArgument<ForestGeneration, TerrainAccess>).GetMethod("Prefix")));
            patcher.Patch(typeof(ServerCacheManager).GetMethod("CleanCache"), prefix: Hook("CleanPrefix"));
        }
        public static void ValidatePatchAttachment()
        {
            Bind();
            var patcher = new HarmonyLib.Harmony("ATT.ForestResetFix.OfflineValidation");
            try { Attach(patcher); }
            finally { patcher.UnpatchSelf(); }
        }
        internal static void MainThread()
        {
            if (Thread.CurrentThread.ManagedThreadId != OwnerThread) throw new InvalidOperationException("Forest operation left the server main thread.");
        }
        internal static void Log(string text) { if (Instance != null) Instance.LoggerInstance.Msg("FOREST_RESET_FIX " + text); }

        public static void Bind()
        {
            if (typeof(ForestGenerator).Module.ModuleVersionId != SupportedGame) throw new NotSupportedException("Unsupported game module MVID.");
            ForestSeed = Field(typeof(ForestGenerator), "seed", typeof(int));
            ForestGenerationTask = Field(typeof(ForestGenerator), "generation", typeof(Task<ForestGeneration>));
            ForestLock = Field(typeof(ForestGenerator), "wipeLock", typeof(TaskCompletionSource<bool>));
            Instructions = Field(typeof(ForestGenerator), "instructions", null);
            Terrain = Field(typeof(ForestGenerator), "terrain", typeof(TerrainInfo));
            RiverTask = Field(typeof(RiverGenerator), "calculateTask", typeof(Task<RiverGeneration>));
            RiverIntersections = Field(typeof(RiverGenerator), "intersections", null);
            RiverValues = Field(typeof(RiverGenerator), "riverValues", typeof(float[,]));
            RiverDepths = Field(typeof(RiverGenerator), "riverDepths", typeof(float[,]));
            RiverEdges = Field(typeof(RiverGenerator), "edgeSegments", null);
            RiverSegments = Field(typeof(RiverGenerator), "segments", null);
            CacheGenerating = Field(typeof(ServerCacheManager), "generating", typeof(Dictionary<string, Task>));
            TerrainModified = Field(typeof(ForestChunk), "isTerrainModified", typeof(bool));
            RequireMethod(typeof(ForestGenerator), "Wipe", typeof(Task), typeof(int));
            RequireMethod(typeof(RiverGenerator), "GetGenerationAsync", typeof(Task<RiverGeneration>), typeof(ForestGeneration), typeof(TerrainAccess));
            RequireMethod(typeof(ServerCacheManager), "CleanCache", typeof(Task), typeof(string));
            if (!typeof(IDictionary).IsAssignableFrom(Instructions.FieldType) || !typeof(IList).IsAssignableFrom(RiverEdges.FieldType)
                || !typeof(IList).IsAssignableFrom(RiverSegments.FieldType)) throw new NotSupportedException("Unexpected collection fields.");
        }

        private static FieldInfo Field(Type type, string name, Type expected)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
            if (field == null || field.IsStatic || (expected != null && field.FieldType != expected)) throw new MissingFieldException(type.FullName, name);
            return field;
        }
        private static void RequireMethod(Type type, string name, Type returns, params Type[] arguments)
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly, null, arguments, null);
            if (method == null || method.ReturnType != returns) throw new MissingMethodException(type.FullName, name);
        }

        internal static IDictionary<string, Task> Tasks()
        {
            var manager = CacheManager.Instance as ServerCacheManager;
            if (manager == null) throw new InvalidOperationException("Server cache manager is not ready.");
            return (IDictionary<string, Task>)CacheGenerating.GetValue(manager);
        }
        private static void BeforeClean(object obj, object[] args)
        {
            if (!NetworkSceneManager.IsServer) return;
            if (Thread.CurrentThread.ManagedThreadId != OwnerThread) return;
            string key = (string)args[0];
            foreach (ForestGenerator forest in ForestGenerator.All)
                if (CacheTasks.Owned(key, forest.Cache)) { RecoverExact(key); return; }
        }
        private static void RecoverExact(string key)
        {
            Task task; var tasks = Tasks();
            if (tasks.TryGetValue(key, out task) && (task.IsFaulted || task.IsCanceled)) tasks.Remove(key);
        }
        private static TerrainAccess BeforeRiver(object obj, ForestGeneration forest, TerrainAccess access)
        {
            if (!NetworkSceneManager.IsServer) return access;
            // Modify normally runs on a worker with a valid snapshot; leave it alone.
            if (access != null) return access;
            MainThread();
            var river = (RiverGenerator)obj;
            Task task = (Task)RiverTask.GetValue(river);
            if (task != null && (task.IsFaulted || task.IsCanceled)) { RiverTask.SetValue(river, null); task = null; }
            RecoverExact(CacheManager.GetCacheName("River Nodes - " + forest.Generator.Cache, null, (int)forest.Seed));
            if (task == null)
            {
                // Cache hits also receive a snapshot; all Unity access stays on this thread.
                TerrainInfo terrain = (TerrainInfo)Terrain.GetValue(forest.Generator);
                if (terrain == null || terrain.TerrainData == null) throw new InvalidOperationException("Forest terrain is not ready.");
                Bounds bounds = forest.Bounds; bounds.center -= forest.Generator.transform.position;
                access = terrain.GetSegment(bounds, true, false);
                if (access.Heights == null || access.HeightsWidth < 2 || access.HeightsHeight < 2) throw new InvalidOperationException("Forest terrain snapshot is empty.");
                Log("supplied terrain snapshot for river cache forest=" + forest.Generator.Cache);
            }
            return access;
        }

        internal static Task Start(ForestGenerator forest, int seed, bool maintenance, Job job)
        {
            MainThread();
            return Gate.Start(forest, seed, maintenance, () => ResetFlow.Run(new GameSteps(forest, seed, maintenance, job), maintenance));
        }

        internal static string StartJob(ForestGenerator forest)
        {
            MainThread();
            if (!Ready || !NetworkSceneManager.IsServer || Gate.IsBusy || MaintenanceHeld)
                return JsonConvert.SerializeObject(new { ok = false, error = "Forest reset is unavailable or maintenance is already active." });
            currentJob = new Job { id = Guid.NewGuid().ToString("N"), state = "running", phase = "starting" };
            Observe(Start(forest, -1, true, currentJob), currentJob);
            return JobJson(currentJob);
        }
        private static async void Observe(Task task, Job job)
        {
            try { await task; job.state = "complete"; job.phase = "awaiting_restart"; }
            catch (Exception error) { job.state = "failed"; job.error = error.GetType().Name + ": " + error.Message; Log("FAILED " + error); }
        }
        internal static string JobStatus(string id)
        {
            MainThread();
            if (currentJob == null || currentJob.id != id) return JsonConvert.SerializeObject(new { ok = false, error = "Unknown forest reset job." });
            return JobJson(currentJob);
        }
        private static string JobJson(Job job)
        {
            return JsonConvert.SerializeObject(new { ok = true, job = job.id, state = job.state, phase = job.phase, error = job.error, restart_required = job.state == "complete" });
        }
        internal static string Status()
        {
            MainThread();
            return JsonConvert.SerializeObject(new { ok = Ready, ready = Ready, version = "0.1.1", hooks = Ready ? 3 : 0,
                server = NetworkSceneManager.IsServer, main_thread = OwnerThread, current_thread = Thread.CurrentThread.ManagedThreadId,
                auto_wipe = false, busy = Gate.IsBusy, maintenance_held = MaintenanceHeld, world_wiping = ServerWipeManager.IsWiping,
                forests = ForestGenerator.All.Select(f => new { name = f.Cache, seed = (int)ForestSeed.GetValue(f),
                    generation = Describe((Task)ForestGenerationTask.GetValue(f)), river = f.RiverGenerator == null ? "missing" : Describe((Task)RiverTask.GetValue(f.RiverGenerator)),
                    native_wipe_lock = ForestLock.GetValue(f) != null, ready_for_reset = ForestReady(f) }).ToArray() });
        }
        private static bool ForestReady(ForestGenerator forest)
        {
            try
            {
            var generated = ForestGenerationTask.GetValue(forest) as Task<ForestGeneration>;
            var river = forest.RiverGenerator == null ? null : RiverTask.GetValue(forest.RiverGenerator) as Task<RiverGeneration>;
            if (generated == null || generated.Status != TaskStatus.RanToCompletion || generated.Result == null
                || river == null || river.Status != TaskStatus.RanToCompletion || river.Result == null || ForestLock.GetValue(forest) != null) return false;
            if (generated.Result.Seed != (uint)(int)ForestSeed.GetValue(forest)) return false;
            foreach (string context in new[] { "Forest Nodes " + forest.Cache, "Forest Terrain - " + forest.Cache, "River Nodes - " + forest.Cache })
            {
                Task pending;
                if (Tasks().TryGetValue(CacheManager.GetCacheName(context, null, (int)generated.Result.Seed), out pending) && !pending.IsCompleted) return false;
            }
            return true;
            }
            catch { return false; }
        }
        private static string Describe(Task task) { return task == null ? "not_started" : task.Status.ToString(); }
        internal sealed class Job { public string id, state, phase, error; }

        private sealed class GameSteps : IResetSteps
        {
            private readonly ForestGenerator forest;
            private readonly int requestedSeed;
            private readonly bool keepLock;
            private readonly Job job;
            private ForestGeneration old;
            private List<LocationChunk> chunks;
            private int seed, oldSeed;
            private ServerJoinLock joinLock;
            private bool entered, success;
            private TaskCompletionSource<bool> generationGate;
            private IEnumerable<string> CacheKeys(int forSeed, IEnumerable<LocationChunk> cacheChunks = null)
            {
                yield return CacheManager.GetCacheName("Forest Nodes " + forest.Cache, null, forSeed);
                yield return CacheManager.GetCacheName("Forest Terrain - " + forest.Cache, null, forSeed);
                yield return CacheManager.GetCacheName("River Nodes - " + forest.Cache, null, forSeed);
                foreach (LocationChunk chunk in cacheChunks ?? chunks) yield return CacheManager.GetCacheName("Forest Static - " + forest.Cache, chunk, forSeed);
            }
            private void CheckCacheTasks(bool recover)
            {
                foreach (string key in CacheKeys(oldSeed).Concat(CacheKeys(seed)).Distinct())
                {
                    if (recover) RecoverExact(key);
                    Task pending;
                    if (Tasks().TryGetValue(key, out pending) && (pending.IsFaulted || pending.IsCanceled || !pending.IsCompleted))
                        throw CacheTasks.Failure(key, pending);
                }
            }
            public GameSteps(ForestGenerator forest, int seed, bool keepLock, Job job) { this.forest = forest; requestedSeed = seed; this.keepLock = keepLock; this.job = job; }
            private void Phase(string value) { MainThread(); if (job != null) job.phase = value; Log("phase=" + value + " forest=" + forest.Cache); }
            private void Empty() { if (Player.AllPlayers.Any()) throw new InvalidOperationException("Players are still connected; use the warned maintenance reset command."); }
            public void Validate()
            {
                MainThread();
                if (!Ready || !NetworkSceneManager.IsServer || forest == null || ServerHandler.Current == null) throw new InvalidOperationException("Forest/server is not ready.");
                if (ServerWipeManager.IsWiping || MaintenanceHeld) throw new InvalidOperationException("Another world wipe or maintenance is active.");
                if (ForestLock.GetValue(forest) != null) throw new InvalidOperationException("A native forest wipe is already active; restart to clear stale state.");
                if (requestedSeed == 0 || requestedSeed < -1) throw new ArgumentOutOfRangeException("seed", "Use -1 for a new random seed or a positive seed.");
                if (forest.RiverGenerator == null || Terrain.GetValue(forest) == null) throw new InvalidOperationException("Forest dependencies are unavailable.");
            }
            public void EnterMaintenance()
            {
                joinLock = new ServerJoinLock { message = "Forest maintenance; please reconnect after the restart", level = 10 };
                ServerHandler.Current.ServerLocks.Add(joinLock); ServerWipeManager.IsWiping = true; entered = true;
            }
            public async Task EnsureNoPlayers(bool disconnect)
            {
                Phase("disconnecting_players");
                if (disconnect) foreach (Player player in Player.AllPlayers.ToArray()) player.Kick("Forest maintenance; please reconnect after the restart");
                else Empty();
                DateTime deadline = DateTime.UtcNow.AddSeconds(60);
                while (Player.AllPlayers.Any()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Players did not disconnect; no forest data deleted."); await Task.Delay(100); MainThread(); }
            }
            public async Task PrepareOldGeneration()
            {
                Phase("validating_generation"); Empty();
                old = await forest.GetGenerationAsync(); MainThread();
                if (old == null) throw new InvalidOperationException("Existing forest generation failed; no forest data deleted.");
                oldSeed = (int)ForestSeed.GetValue(forest);
                if (old.Seed != (uint)oldSeed) throw new InvalidOperationException("Existing forest seed/generation disagree; no forest data deleted.");
                chunks = LocationChunkHelper.GetAllContaining(old.Bounds).Distinct().ToList();
                if (chunks.Count == 0 || chunks.Any(c => c == null || c.IsGlobal)) throw new InvalidOperationException("Invalid forest chunk bounds; no forest data deleted.");
                seed = requestedSeed > 0 ? requestedSeed : NewSeed(oldSeed);
            }
            public async Task QuiesceChunks()
            {
                Phase("waiting_for_unload"); Empty();
                foreach (LocationChunk chunk in chunks) chunk.IsForceLoaded = false;
                DateTime deadline = DateTime.UtcNow.AddSeconds(90);
                while (chunks.Any(c => !c.IsFullyUnloaded))
                {
                    Empty();
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException("Forest chunks remain loaded; no forest data deleted.");
                    await Task.Delay(100); MainThread();
                }
                if (((IDictionary)Instructions.GetValue(forest)).Count != 0) throw new InvalidOperationException("Forest static instructions remain active; no forest data deleted.");
                await AltaFile.FinishAllAsync(); MainThread();
                CheckCacheTasks(true);
            }
            public void HoldGeneration() { Phase("clearing_old_data"); Empty(); generationGate = new TaskCompletionSource<bool>(); ForestLock.SetValue(forest, generationGate); }
            public async Task ClearOldData()
            {
                Empty(); CheckCacheTasks(true);
                foreach (LocationChunk chunk in chunks)
                {
                    if (!chunk.IsFullyUnloaded) throw new InvalidOperationException("Forest chunk reloaded during maintenance.");
                    await chunk.ClearSave(); MainThread();
                    await CacheManager.Instance.CleanCache(CacheManager.GetCacheName("Forest Static - " + forest.Cache, chunk, oldSeed)); MainThread();
                    if (seed != oldSeed) { await CacheManager.Instance.CleanCache(CacheManager.GetCacheName("Forest Static - " + forest.Cache, chunk, seed)); MainThread(); }
                }
                await CacheManager.Instance.CleanCache(CacheManager.GetCacheName("Forest Nodes " + forest.Cache, null, oldSeed)); MainThread();
                await CacheManager.Instance.CleanCache(CacheManager.GetCacheName("Forest Terrain - " + forest.Cache, null, oldSeed)); MainThread();
                await CacheManager.Instance.CleanCache(CacheManager.GetCacheName("River Nodes - " + forest.Cache, null, oldSeed)); MainThread();
                if (seed != oldSeed)
                {
                    await CacheManager.Instance.CleanCache(CacheManager.GetCacheName("Forest Nodes " + forest.Cache, null, seed)); MainThread();
                    await CacheManager.Instance.CleanCache(CacheManager.GetCacheName("Forest Terrain - " + forest.Cache, null, seed)); MainThread();
                    await CacheManager.Instance.CleanCache(CacheManager.GetCacheName("River Nodes - " + forest.Cache, null, seed)); MainThread();
                }
            }
            public void SelectSeedAndInvalidate()
            {
                Phase("invalidating_generation"); Empty();
                ForestSeed.SetValue(forest, seed); ForestGenerationTask.SetValue(forest, null);
                RiverGenerator river = forest.RiverGenerator;
                RiverTask.SetValue(river, null); RiverIntersections.SetValue(river, null); RiverValues.SetValue(river, null); RiverDepths.SetValue(river, null);
                ((IList)RiverEdges.GetValue(river)).Clear(); ((IList)RiverSegments.GetValue(river)).Clear();
                foreach (LocationChunk chunk in chunks)
                {
                    var stream = chunk.ProceduralStreamer as ForestChunk;
                    if (stream != null) TerrainModified.SetValue(stream, false);
                }
            }
            public void ReleaseGeneration()
            {
                MainThread();
                if (generationGate == null) return;
                if (ReferenceEquals(ForestLock.GetValue(forest), generationGate)) ForestLock.SetValue(forest, null);
                generationGate.TrySetResult(true); generationGate = null;
            }
            public async Task RebuildAndValidate()
            {
                Phase("regenerating"); Empty();
                ForestGeneration rebuilt = await forest.GetGenerationAsync(); MainThread();
                if (rebuilt == null || rebuilt.Seed != (uint)seed) throw new InvalidOperationException("Forest regeneration failed or returned the old seed.");
                Task<RiverGeneration> river = (Task<RiverGeneration>)RiverTask.GetValue(forest.RiverGenerator);
                if (river == null || await river == null) throw new InvalidOperationException("River regeneration did not complete.");
                MainThread(); Empty();
                string[] rebuiltKeys = CacheKeys(seed, LocationChunkHelper.GetAllContaining(rebuilt.Bounds).Distinct()).ToArray();
                Phase("waiting_for_cache");
                try
                {
                    await CacheTasks.WaitForCompletion(Tasks(), CacheKeys(oldSeed).Concat(rebuiltKeys).Distinct().ToArray(),
                        () => { MainThread(); Empty(); });
                }
                finally
                {
                    // Native cache wrappers queue their writes before removing
                    // successful registry entries. Drain writes even on failure.
                    await AltaFile.FinishAllAsync(); MainThread();
                }
                Empty(); CheckCacheTasks(false);
                foreach (string key in rebuiltKeys)
                {
                    IAltaFile file;
                    if (!ServerHandler.Current.SaveUtility.CacheFolder.FileExists(key, out file))
                        throw new InvalidOperationException("Regenerated cache file is missing: " + key);
                }
            }
            public async Task Persist()
            {
                Phase("saving");
                // ForestSeedUtil would otherwise force the old preference at next startup.
                Type main = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("ForestSeedUtil.Main", false)).FirstOrDefault(t => t != null);
                if (main != null)
                {
                    var property = main.GetProperty("Seed", BindingFlags.Public | BindingFlags.Static);
                    var entry = property == null ? null : property.GetValue(null, null) as MelonPreferences_Entry<int>;
                    if (entry == null) throw new InvalidOperationException("ForestSeedUtil seed preference is unavailable; reset is not committed.");
                    entry.EditedValue = seed; MelonPreferences.Save();
                }
                Chunk[] globals = Chunk.ChunksByIdentifier.Values.Where(c => c.IsGlobal).ToArray();
                if (globals.Length == 0 || globals.Any(c => c.ContentManager == null)) throw new InvalidOperationException("Global chunk save manager is unavailable.");
                // Chunk.Save swallows native exceptions; use its native manager directly.
                foreach (Chunk global in globals) { await global.ContentManager.Save(); MainThread(); }
                await AltaFile.FinishAllAsync(); MainThread(); success = true;
                Log("COMPLETE forest=" + forest.Cache + " old_seed=" + oldSeed + " new_seed=" + seed + " chunks=" + chunks.Count + " restart_required=" + keepLock);
            }
            public void ExitMaintenance()
            {
                MainThread();
                if (!entered) return;
                ServerWipeManager.IsWiping = false;
                if (success && keepLock) { MaintenanceHeld = true; return; }
                ServerHandler.Current.ServerLocks.Remove(joinLock); entered = false;
            }
            private static int NewSeed(int previous) { int result; do { result = new System.Random().Next(1, int.MaxValue); } while (result == previous); return result; }
        }
    }

    [Module("forestresetfix", "Forest reset maintenance and read-only status")]
    [ServerOnly]
    public static class Commands
    {
        [Command("status", "Read-only fix and forest generation status")]
        public static string Status() { return Core.Status(); }
        [Command("reset", "After warning players, disconnect them and regenerate forest; restart is required after completion")]
        public static string Reset(ForestGenerator forest) { return Core.StartJob(forest); }
        [Command("job", "Read-only status of the current forest maintenance job")]
        public static string Job(string id) { return Core.JobStatus(id); }
    }
}
