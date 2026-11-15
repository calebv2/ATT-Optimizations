using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Alta.Intelligence;
using Alta.Networking;
using Alta.Pathfinding;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace MobCombatDiagnostics
{
    public sealed class Core : CavePerformance.Support.Module
    {
        internal sealed class Mob
        {
            internal long Id;
            internal Agent Agent = null!;
            internal NetworkEntity Root = null!;
            internal Navigator? Navigator;
            internal string Chunk = "";
            internal int Layer;
            internal bool Active = true;
            internal double NextSample, LastCache, LastTransform, LastCopy, LastSnapshot;
            internal Vector3 LastPosition;
            internal long CacheCalls, TransformCalls, CopyCalls, ChannelCalls, DamageCalls;
            internal readonly RateGate Channels = new RateGate(4), Damage = new RateGate(8), Transforms = new RateGate(1), Caches = new RateGate(1), Copies = new RateGate(4);
        }
        private sealed class Pending { internal WeakReference Agent = null!; internal double Expires; }
        private sealed class Mark { public Mark() { } }
        internal static volatile bool Ready, DisableRequested;
        internal static int MainThread;
        internal static bool CanObserve => Ready && !DisableRequested && Thread.CurrentThread.ManagedThreadId == MainThread;
        private static readonly long Origin = Stopwatch.GetTimestamp();
        internal static double Now => (Stopwatch.GetTimestamp() - Origin) * 1000.0 / Stopwatch.Frequency;
        internal static readonly PacketScopes Packets = new PacketScopes();
        internal static readonly ConditionalWeakTable<object, Token> Peers = new ConditionalWeakTable<object, Token>();
        internal static readonly ConditionalWeakTable<object, Token> Targets = new ConditionalWeakTable<object, Token>();
        internal static readonly ConditionalWeakTable<EntitySerializer, Mob> Serializers = new ConditionalWeakTable<EntitySerializer, Mob>();
        private static readonly ConditionalWeakTable<Agent, Mark> Seen = new ConditionalWeakTable<Agent, Mark>();
        private static readonly Dictionary<NetworkEntity, Mob> Roots = new Dictionary<NetworkEntity, Mob>();
        private static readonly List<Mob> Mobs = new List<Mob>();
        private static readonly List<Pending> PendingAgents = new List<Pending>();
        private static readonly RateGate GlobalRows = new RateGate(512);
        private static long nextMob;
        internal static long RateLimited, CapacitySkipped, OffThread, SlowSnapshots;
        private static int cursor;
        private static bool snapshotsDisabled, warned;
        private static double nextHealth, previousUpdate;
        private HarmonyLib.Harmony? harmony;
        internal static ServerTrace? Events, Replication;
        private static ServerTrace? snapshots, health;
        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            MainThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                Hooks.Validate();
                string dir = Path.Combine(DataDirectory, "Diagnostics", "MobCombatDiagnostics", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
                Directory.CreateDirectory(dir);
                Events = new ServerTrace(Path.Combine(dir, "events.csv"), "utc,mono_ms,kind,mob_id,root_entity_id,layer,chunk,peer_token,packet_id,duration_ms,detail,exception_type,dropped_rows");
                Replication = new ServerTrace(Path.Combine(dir, "replication.csv"), "utc,mono_ms,stage,mob_id,root_entity_id,entity_id,layer,peer_token,packet_id,bits_or_bytes,duration_ms,detail,exception_type,dropped_rows");
                snapshots = new ServerTrace(Path.Combine(dir, "snapshots.csv"), "utc,mono_ms,mob_id,root_entity_id,agent_type,layer,chunk,x,y,z,vx,vy,vz,kinematic,walker_enabled,walker_paused,walker_power,path_pending,path_age_s,path_finished,next_x,next_y,next_z,action_type,acting,spawned_peer_count,sample_elapsed_ms,moved_world_units,cache_calls,transform_calls,copy_calls,channel_calls,damage_calls,cache_age_ms,transform_age_ms,copy_age_ms,dropped_rows");
                health = new ServerTrace(Path.Combine(dir, "health.csv"), "utc,mono_ms,tracked,pending,rate_limited,capacity_skipped,off_thread_callbacks,probe_errors,slow_snapshots,snapshots_disabled,event_drops,replication_drops,snapshot_drops,health_drops,writer_error,state");
                harmony = new HarmonyLib.Harmony("ATT.CavePerformance.MobCombatDiagnostics");
                Hooks.Attach(harmony, Path.Combine(dir, "hooks.csv"));
                EntitySerializer.WroteEntity += Hooks.Cached;
                Ready = true;
                File.WriteAllText(Path.Combine(dir, "run.txt"), "Mob Combat Diagnostics 0.1.0\nGame MVID " + typeof(Agent).Module.ModuleVersionId + "\nCave layers >=" + CavePerformance.Core.Config.DiagnosticMinLayer + "; max 128 tracked mobs, 256 pending; snapshots about 1s per mob with max 4 per update and 2ms between-unit budget; rows globally limited to 512/s. No scene scans.\nAll observations are server-side. Channel calls are not necessarily attacks. Cached/assembled/send/transport-return stages do not prove receipt or rendering on clients. Packet IDs join scene serialization assembly to Connection.Send for the same writer, not to a unique wire datagram. Transport rows correlate only by peer and time.\nHealth differences are observed around ReceiveDamage, not a causal attribution of every health change. Player accounts, addresses, payload contents and target entity IDs are not recorded. Entity IDs are server mob IDs; peer/target tokens are opaque per-run identifiers.\n");
                LoggerInstance.Msg("READY embedded; cave layers >=" + CavePerformance.Core.Config.DiagnosticMinLayer + "; bounded NPC state, event/damage and replication observations. No combat or networking behavior changed. Output: " + dir);
            }
            catch (Exception e)
            {
                LoggerInstance.Error("Mob combat diagnostics DISABLED: " + e);
                Stop();
            }
        }
        internal static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        internal static string Action(Agent agent) => agent.ActiveAction?.GetType().Name ?? "";
        internal static Vector3 Position(Mob mob)
        {
            var walker = mob.Agent.Walker;
            var body = walker != null && walker.topic != null ? walker.topic : mob.Agent.Rigidbody;
            return body != null ? body.position : mob.Root.transform.position;
        }
        internal static double Age(double now, double observed) => observed == 0 ? -1 : now - observed;
        internal static void Event(string kind, Mob? mob, double start, string detail, Exception? error = null, long peer = 0, long packet = 0)
        {
            double now = Now;
            if (!GlobalRows.Take(now)) { RateLimited++; return; }
            var sink = Events;
            if (sink == null) return;
            sink.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), F(now), kind, mob?.Id ?? 0, mob?.Root.Identifier ?? 0,
                mob?.Layer ?? -1, mob?.Chunk ?? "", peer, packet, F(now - start), detail, error?.GetType().FullName ?? "", sink.Dropped));
        }
        internal static void Replicate(string stage, Mob? mob, uint entity, long peer, long packet, long size, double start, string detail, Exception? error = null)
        {
            double now = Now;
            if (!GlobalRows.Take(now)) { RateLimited++; return; }
            var sink = Replication;
            if (sink == null) return;
            sink.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), F(now), stage, mob?.Id ?? 0, mob?.Root.Identifier ?? 0,
                entity, mob?.Layer ?? -1, peer, packet, size, F(now - start), detail, error?.GetType().FullName ?? "", sink.Dropped));
        }
        internal static Mob? Find(NetworkEntity? entity)
        {
            if (entity == null) return null;
            var root = entity.PrefabRoot;
            return root != null && Roots.TryGetValue(root, out var mob) && mob.Active ? mob : null;
        }
        internal static Mob? Find(Agent agent) => agent == null ? null : Find(agent.Entity);
        internal static void TrackRegistered(Agent agent)
        {
            if (agent == null || Seen.TryGetValue(agent, out _)) return;
            Seen.Add(agent, new Mark());
            if (!TryTrack(agent))
            {
                if (PendingAgents.Count < 256) PendingAgents.Add(new Pending { Agent = new WeakReference(agent), Expires = Now + 30000 });
                else CapacitySkipped++;
            }
        }
        private static bool TryTrack(Agent agent)
        {
            if (agent == null || agent.Entity == null) return true;
            NetworkEntity root = agent.Entity.PrefabRoot;
            if (root == null || root.Chunk == null) return false;
            string chunk = root.Chunk.ChunkIdentifier;
            if (!Probe.CaveLevel(chunk, out int level)) return false; // chunk assignment may follow AI initialization
            if (level < CavePerformance.Core.Config.DiagnosticMinLayer || Roots.ContainsKey(root)) return true;
            if (Mobs.Count >= 128)
            {
                int evict = Mobs.FindIndex(m => m.Root == null || m.Agent == null || m.Root.PlayersSpawnedForCount == 0);
                if (evict < 0) { CapacitySkipped++; return true; }
                Remove(evict);
            }
            var mob = new Mob { Id = ++nextMob, Agent = agent, Root = root, Chunk = chunk, Layer = level };
            if (agent.Walker != null) mob.Navigator = agent.Walker.GetComponent<Navigator>();
            Roots.Add(root, mob); Mobs.Add(mob);
            Event("tracked", mob, Now, "agent_type=" + agent.GetType().Name);
            return true;
        }
        private static void Remove(int index)
        {
            var mob = Mobs[index]; mob.Active = false;
            Roots.Remove(mob.Root); Mobs.RemoveAt(index);
            if (cursor >= Mobs.Count) cursor = 0;
        }
        private static void Snapshot(Mob mob, double now)
        {
            var agent = mob.Agent; var root = mob.Root;
            if (root.Chunk == null || !Probe.CaveLevel(root.Chunk.ChunkIdentifier, out int depth) || depth < CavePerformance.Core.Config.DiagnosticMinLayer) return;
            mob.Chunk = root.Chunk.ChunkIdentifier; mob.Layer = depth;
            var walker = agent.Walker; var nav = mob.Navigator;
            var body = walker != null && walker.topic != null ? walker.topic : agent.Rigidbody;
            Vector3 position = body != null ? body.position : root.transform.position;
            Vector3 velocity = body != null ? body.velocity : Vector3.zero;
            Vector3 next = nav != null ? nav.NextPoint : Vector3.zero;
            double elapsed = mob.LastSnapshot == 0 ? -1 : now - mob.LastSnapshot;
            double moved = mob.LastSnapshot == 0 ? -1 : Vector3.Distance(position, mob.LastPosition);
            if (snapshots != null && GlobalRows.Take(now))
                snapshots.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), F(now), mob.Id, root.Identifier, agent.GetType().Name, depth, mob.Chunk,
                    F(position.x), F(position.y), F(position.z), F(velocity.x), F(velocity.y), F(velocity.z), body != null ? (object)body.isKinematic : "",
                    walker != null ? (object)walker.enabled : "", walker != null ? (object)walker.IsPaused : "", walker != null ? (object)walker.Power : "",
                    nav != null ? (object)nav.IsRequestingPath : "", nav != null && nav.IsRequestingPath ? (object)F(Time.time - nav.LastPathRequest) : "",
                    nav != null ? (object)nav.IsFinished : "", nav != null ? (object)F(next.x) : "", nav != null ? (object)F(next.y) : "", nav != null ? (object)F(next.z) : "",
                    Action(agent), agent.IsActing, root.PlayersSpawnedForCount, F(elapsed), F(moved), mob.CacheCalls, mob.TransformCalls, mob.CopyCalls, mob.ChannelCalls, mob.DamageCalls,
                    F(Age(now, mob.LastCache)), F(Age(now, mob.LastTransform)), F(Age(now, mob.LastCopy)), snapshots.Dropped));
            else RateLimited++;
            mob.LastPosition = position; mob.LastSnapshot = now;
        }
        public override void OnUpdate()
        {
            if (!Ready) return;
            if (DisableRequested || Events?.Error != null || Replication?.Error != null || snapshots?.Error != null || health?.Error != null)
            {
                LoggerInstance.Warning("Mob diagnostics disabled after writer failure or repeated probe errors; errors=" + Probe.Errors);
                Stop(); return;
            }
            double start = Now;
            if (previousUpdate != 0 && start - previousUpdate >= 500) Event("main_update_gap", null, previousUpdate, "gap_ms=" + F(start - previousUpdate));
            previousUpdate = start;
            try
            {
                for (int n = 0; n < 4 && PendingAgents.Count > 0 && Now - start < 2; n++)
                {
                    var pending = PendingAgents[0]; PendingAgents.RemoveAt(0);
                    if (pending.Agent.Target is Agent agent && agent != null && Now < pending.Expires && !TryTrack(agent)) PendingAgents.Add(pending);
                }
                int checkedCount = 0, sampled = 0, count = Mobs.Count;
                while (!snapshotsDisabled && checkedCount++ < count && Mobs.Count > 0 && sampled < 4 && Now - start < 2)
                {
                    if (cursor >= Mobs.Count) cursor = 0;
                    var mob = Mobs[cursor];
                    if (mob.Agent == null || mob.Root == null) { Remove(cursor); continue; }
                    cursor++;
                    if (Now < mob.NextSample) continue;
                    double before = Now; mob.NextSample = before + 1000;
                    Snapshot(mob, before); sampled++;
                    if (Now - before > 10) { SlowSnapshots++; snapshotsDisabled = true; }
                }
                if (start >= nextHealth)
                {
                    nextHealth = start + 60000;
                    string error = Events?.Error ?? Replication?.Error ?? snapshots?.Error ?? health?.Error ?? "";
                    health?.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), F(start), Mobs.Count, PendingAgents.Count, RateLimited, CapacitySkipped,
                        OffThread, Interlocked.Read(ref Probe.Errors), SlowSnapshots, snapshotsDisabled, Events?.Dropped ?? 0, Replication?.Dropped ?? 0, snapshots?.Dropped ?? 0, health?.Dropped ?? 0, error, "running"));
                    if (!warned && (error.Length > 0 || Probe.Errors > 0 || snapshotsDisabled))
                    { warned = true; LoggerInstance.Warning("Mob diagnostic health degraded; inspect health.csv. No gameplay recovery attempted."); }
                    if (error.Length > 0 || Probe.Errors >= 50) { LoggerInstance.Warning("Mob diagnostics disabled after writer failure or repeated probe errors."); Stop(); }
                }
            }
            catch { if (Interlocked.Increment(ref Probe.Errors) >= 50) DisableRequested = true; }
        }
        private void Stop(bool wait = false)
        {
            Ready = false;
            health?.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), F(Now), Mobs.Count, PendingAgents.Count, RateLimited, CapacitySkipped,
                OffThread, Interlocked.Read(ref Probe.Errors), SlowSnapshots, snapshotsDisabled, Events?.Dropped ?? 0, Replication?.Dropped ?? 0,
                snapshots?.Dropped ?? 0, health?.Dropped ?? 0, Events?.Error ?? Replication?.Error ?? snapshots?.Error ?? health?.Error ?? "", "shutdown"));
            EntitySerializer.WroteEntity -= Hooks.Cached;
            try { harmony?.UnpatchSelf(); } catch (Exception e) { LoggerInstance.Error("Diagnostic unpatch failed: " + e.GetType().Name); }
            foreach (var mob in Mobs) mob.Active = false;
            Mobs.Clear(); Roots.Clear(); PendingAgents.Clear();
            Events?.Stop(wait); Replication?.Stop(wait); snapshots?.Stop(wait); health?.Stop(wait);
            Events = Replication = snapshots = health = null;
        }
        public override void OnApplicationQuit() => Stop(true);
    }
}
