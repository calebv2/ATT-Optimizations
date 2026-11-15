using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Alta.Impact;
using Alta.Intelligence;
using Alta.Networking;
using Alta.Networking.Internal;
using HarmonyLib;
using UnityEngine;
using GameStream = Alta.Serialization.Stream;
using GameWriter = Alta.Serialization.StreamWriter;

namespace MobCombatDiagnostics
{
    internal static class Hooks
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static FieldInfo cachedPosition = null!, worldBased = null!, lastSyncTime = null!, healthStat = null!, outgoingQueue = null!, queueMode = null!;
        private static PropertyInfo healthValue = null!;
        private static readonly (Type type, string method, int args, string? prefix, string? finalizer, string? postfix)[] Specs = {
            (typeof(Agent), "InitializeAsServer", 1, null, null, nameof(Registered)),
            (typeof(Agent), "FireChannelEvent", 2, nameof(ChannelBegin), nameof(ChannelEnd), null),
            (typeof(Agent), "SyncEvent", 3, null, nameof(EventSerialized), null),
            (typeof(NetworkTransform), "Serialize", 2, nameof(TransformBegin), nameof(TransformEnd), null),
            (typeof(SceneSerializer), "SerializeWrite", 3, nameof(PacketBegin), nameof(PacketEnd), null),
            (typeof(EntitySerializer), "WriteDataIntoStream", 1, nameof(CopyBegin), nameof(CopyEnd), null),
            (typeof(Connection), "Send", 3, nameof(SendBegin), nameof(SendEnd), null),
            (typeof(Connection), "InternalSend", 5, nameof(TransportBegin), nameof(TransportEnd), null),
            (typeof(HealthObject), "ReceiveDamage", 3, nameof(DamageBegin), nameof(DamageEnd), null)
        };
        internal static MethodInfo Resolve(Type type, string name, int args)
        {
            var methods = type.GetMethods(Flags).Where(m => m.Name == name && m.GetParameters().Length == args);
            if (type == typeof(Connection) && name == "Send")
                methods = methods.Where(m => m.GetParameters()[1].ParameterType == typeof(GameWriter));
            return methods.Single();
        }
        internal static void Validate()
        {
            if (typeof(Agent).Module.ModuleVersionId != new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff")) throw new NotSupportedException("Unsupported game module");
            cachedPosition = Required(typeof(NetworkTransform), "position", typeof(Vector3));
            worldBased = Required(typeof(NetworkTransform), "isWorldBasedPosition", typeof(bool));
            lastSyncTime = Required(typeof(NetworkTransform), "lastSyncTime", typeof(float));
            healthStat = typeof(HealthObject).GetField("healthStat", Flags) ?? throw new MissingFieldException("healthStat");
            healthValue = healthStat.FieldType.GetProperty("Value", Flags) ?? throw new MissingMemberException("cached health stat Value");
            if (healthValue.PropertyType != typeof(float)) throw new NotSupportedException("Health stat Value type changed");
            outgoingQueue = Required(typeof(Connection), "outgoingQueueSize", typeof(int));
            queueMode = Required(typeof(Connection), "IsQueuingSends", typeof(bool));
            foreach (var spec in Specs) Resolve(spec.type, spec.method, spec.args);
        }
        private static FieldInfo Required(Type type, string name, Type expected)
        {
            var f = type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name);
            if (f.IsStatic || f.FieldType != expected) throw new NotSupportedException("Unexpected field " + name);
            return f;
        }
        internal static void Attach(HarmonyLib.Harmony harmony, string hooksFile)
        {
            File.WriteAllText(hooksFile, "target,status\n");
            HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(typeof(Hooks), name);
            foreach (var spec in Specs)
            {
                var method = Resolve(spec.type, spec.method, spec.args);
                harmony.Patch(method, prefix: Hook(spec.prefix), finalizer: Hook(spec.finalizer), postfix: Hook(spec.postfix));
                File.AppendAllText(hooksFile, ServerTrace.Row(method.DeclaringType!.FullName + "." + method.Name, "attached") + "\n");
            }
            File.AppendAllText(hooksFile, "EntitySerializer.WroteEntity,subscribed-after-attachment\n");
        }
        internal struct Call
        {
            internal Core.Mob? Mob;
            internal double Start;
            internal long Peer, Target;
            internal Packet? Packet, Previous;
            internal string? Detail;
            internal bool Scoped;
        }
        private static bool Main()
        {
            if (Core.CanObserve) return true;
            if (Core.Ready) Interlocked.Increment(ref Core.OffThread);
            return false;
        }
        private static void Error() { if (Interlocked.Increment(ref Probe.Errors) >= 50) Core.DisableRequested = true; }
        private static Token Peer(Connection connection) => Core.Peers.GetOrCreateValue((object?)connection.Player ?? connection);
        private static void Registered(Agent __instance)
        {
            if (!Main()) return;
            try { Core.TrackRegistered(__instance); } catch { Error(); }
        }
        private static void ChannelBegin(Agent __instance, HookEventChannel __0, IActiveAction __1, out Call __state)
        {
            __state = default;
            if (!Main()) return;
            try
            {
                var mob = Core.Find(__instance); if (mob == null) return;
                mob.ChannelCalls++;
                double now = Core.Now;
                if (!mob.Channels.Take(now)) { Core.RateLimited++; return; }
                int index = (int)__0;
                var channels = __instance.ChannelEvents;
                string networked = index >= 0 && index < channels.Length ? channels[index].IsNetworked.ToString() : "unknown";
                __state = new Call { Mob = mob, Start = now, Detail = "channel=" + index + ";action=" + (__1?.GetType().Name ?? Core.Action(__instance)) + ";networked_config=" + networked + ";mob_world=" + XYZ(Core.Position(mob)) };
                Core.Event("agent_channel_begin", mob, now, __state.Detail);
            }
            catch { Error(); }
        }
        private static void ChannelEnd(Call __state, Exception? __exception)
        {
            if (__state.Mob == null) return;
            try { Core.Event("agent_channel_return", __state.Mob, __state.Start, __state.Detail ?? "", __exception); } catch { Error(); }
        }
        private static void EventSerialized(Agent __instance, object? __0, GameStream __1, int __2, Exception? __exception)
        {
            if (!Main()) return;
            try
            {
                if (!__1.IsWriting) return;
                var mob = Core.Find(__instance); if (mob == null) return;
                long peer = __0 == null ? 0 : Core.Peers.GetOrCreateValue(__0).Id;
                Core.Event("agent_event_serialize_call", mob, Core.Now, "channel=" + __2, __exception, peer);
            }
            catch { Error(); }
        }
        private static void TransformBegin(NetworkTransform __instance, GameStream __0, bool __1, out Call __state)
        {
            __state = default;
            if (!Main()) return;
            try
            {
                if (!__0.IsWriting) return;
                var mob = Core.Find(__instance.Entity); if (mob == null) return;
                mob.TransformCalls++; mob.LastTransform = Core.Now;
                if (!mob.Transforms.Take(Core.Now)) { Core.RateLimited++; return; }
                bool valueGate = (__instance.Entity.Serializer != null && __instance.Entity.Serializer.IsAuthoritative) || (float)lastSyncTime.GetValue(__instance) != 0;
                __state = new Call { Mob = mob, Start = Core.Now, Detail = "value_gate_before=" + valueGate + ";is_full=" + __1 };
            }
            catch { Error(); }
        }
        private static void TransformEnd(NetworkTransform __instance, Call __state, Exception? __exception)
        {
            if (__state.Mob == null) return;
            try
            {
                var raw = (Vector3)cachedPosition.GetValue(__instance);
                var target = __instance.TargetTransform;
                string position = target == null ? "target_missing=true" : "target_world=" + XYZ(target.position);
                Core.Replicate("transform_serialize_call", __state.Mob, __instance.Entity.Identifier, 0, 0, 0, __state.Start,
                    __state.Detail + ";cached_raw=" + XYZ(raw) + ";world_based=" + worldBased.GetValue(__instance) + ";" + position, __exception);
            }
            catch { Error(); }
        }
        private static string XYZ(Vector3 v) => Core.F(v.x) + "/" + Core.F(v.y) + "/" + Core.F(v.z);
        internal static void Cached(EntitySerializer serializer, NetworkEntity entity, uint[] data, uint bitlength)
        {
            if (!Main()) return;
            try
            {
                var mob = Core.Find(entity); if (mob == null) return;
                if (!Core.Serializers.TryGetValue(serializer, out var previous) || !ReferenceEquals(previous, mob))
                { Core.Serializers.Remove(serializer); Core.Serializers.Add(serializer, mob); }
                mob.CacheCalls++; mob.LastCache = Core.Now;
                if (!mob.Caches.Take(Core.Now)) { Core.RateLimited++; return; }
                Core.Replicate("entity_cache_callback", mob, entity.Identifier, 0, 0, bitlength, Core.Now, "cached_payload_not_necessarily_new_transform");
            }
            catch { Error(); }
        }
        private static void PacketBegin(GameWriter __0, Connection __1, out Call __state)
        {
            __state = default;
            if (!Main()) return;
            try
            {
                Core.Packets.Consume(__0); // discard stale marker if a writer was reused without a send
                var packet = new Packet(__0, Peer(__1).Id, Core.Now);
                __state = new Call { Packet = packet, Previous = PacketScopes.Current, Scoped = true };
                PacketScopes.Current = packet;
            }
            catch { Error(); }
        }
        private static void PacketEnd(Call __state, Exception? __exception)
        {
            if (!__state.Scoped) return;
            try
            {
                var packet = __state.Packet!;
                if (packet.Relevant)
                {
                    if (__exception == null) Core.Packets.Remember(packet);
                    Core.Replicate("scene_packet_assembled", null, 0, packet.Peer, packet.Id, 0, packet.Started, "contains_tracked_entity_copy_calls", __exception);
                }
            }
            catch { Error(); }
            finally { PacketScopes.Current = __state.Previous; }
        }
        private static void CopyBegin(EntitySerializer __instance, out Call __state)
        {
            __state = default;
            if (!Main() || PacketScopes.Current == null) return;
            try
            {
                if (!Core.Serializers.TryGetValue(__instance, out var mob) || !mob.Active) return;
                var packet = PacketScopes.Current; packet.Relevant = true;
                mob.CopyCalls++; mob.LastCopy = Core.Now;
                if (!mob.Copies.Take(Core.Now)) { Core.RateLimited++; return; }
                __state = new Call { Mob = mob, Start = Core.Now, Packet = packet };
            }
            catch { Error(); }
        }
        private static void CopyEnd(EntitySerializer __instance, Call __state, Exception? __exception)
        {
            if (__state.Mob == null) return;
            try { Core.Replicate("entity_copy_call", __state.Mob, __instance.Identifier, __state.Packet!.Peer, __state.Packet.Id, __instance.DataBitCount, __state.Start, "scene_packet_payload", __exception); }
            catch { Error(); }
        }
        private static void SendBegin(Connection __instance, GameWriter __1, out Call __state)
        {
            __state = default;
            if (!Main()) return;
            try
            {
                var packet = Core.Packets.Consume(__1); if (packet == null) return;
                var peer = Peer(__instance);
                if (packet.Peer != peer.Id) { Error(); return; }
                peer.RelevantUntil = Core.Now + 10000;
                __state = new Call { Packet = packet, Peer = peer.Id, Start = Core.Now };
            }
            catch { Error(); }
        }
        private static void SendEnd(Connection __instance, int __2, bool __result, Call __state, Exception? __exception)
        {
            if (__state.Packet == null) return;
            try { Core.Replicate("connection_send_return", null, 0, __state.Peer, __state.Packet.Id, __2, __state.Start,
                "api_result=" + __result + ";queued_mode=" + queueMode.GetValue(__instance) + ";sampled_transport_queue=" + outgoingQueue.GetValue(__instance) + ";assembly_to_send_ms=" + Core.F(__state.Start - __state.Packet.Started), __exception); }
            catch { Error(); }
        }
        private static void TransportBegin(Connection __instance, out Call __state)
        {
            __state = default;
            if (!Main()) return;
            try
            {
                var peer = Peer(__instance);
                if (Core.Now > peer.RelevantUntil || peer.RelevantUntil == 0) return;
                peer.TransportCalls++;
                if (!peer.TransportRate.Take(Core.Now)) { Core.RateLimited++; return; }
                __state = new Call { Peer = peer.Id, Start = Core.Now, Detail = "recent_tracked_scene_send;peer_transport_calls=" + peer.TransportCalls };
            }
            catch { Error(); }
        }
        private static void TransportEnd(int __1, bool __result, Call __state, Exception? __exception)
        {
            if (__state.Start == 0) return;
            try { Core.Replicate("transport_api_return", null, 0, __state.Peer, 0, __1, __state.Start, __state.Detail + ";api_result=" + __result + ";packet_association=unknown", __exception); }
            catch { Error(); }
        }
        // Read the already-existing stat only. HealthObject.Health can create a missing stat;
        // the observer must never trigger that lazy initialization.
        private static string ExistingHealth(HealthObject target)
        {
            object? stat = healthStat.GetValue(target);
            return stat == null ? "unavailable" : Core.F((float)healthValue.GetValue(stat, null));
        }
        private static void DamageBegin(HealthObject __instance, float __0, float __1, DamageData __2, out Call __state)
        {
            __state = default;
            if (!Main()) return;
            try
            {
                if (!(__2 is ImpactHit hit)) return;
                var mob = Core.Find(hit.Wielder); if (mob == null) return;
                mob.DamageCalls++;
                double now = Core.Now;
                if (!mob.Damage.Take(now)) { Core.RateLimited++; return; }
                Vector3 position = Core.Position(mob);
                var nav = mob.Navigator;
                __state = new Call { Mob = mob, Start = now, Target = Core.Targets.GetOrCreateValue(__instance).Id,
                    Detail = "requested_health=" + Core.F(__0) + ";requested_cripple=" + Core.F(__1) + ";health_before=" + ExistingHealth(__instance)
                        + ";mob_world=" + XYZ(position) + ";target_transform_distance=" + Core.F(Vector3.Distance(position, __instance.transform.position))
                        + ";action=" + Core.Action(mob.Agent) + ";nav_pending=" + (nav == null ? "unknown" : nav.IsRequestingPath.ToString()) };
                Core.Event("npc_damage_begin", mob, now, "target_token=" + __state.Target + ";" + __state.Detail);
            }
            catch { Error(); }
        }
        private static void DamageEnd(HealthObject __instance, Call __state, Exception? __exception)
        {
            if (__state.Mob == null) return;
            try { Core.Event("npc_damage_return", __state.Mob, __state.Start,
                "target_token=" + __state.Target + ";" + __state.Detail + ";health_after=" + ExistingHealth(__instance), __exception); }
            catch { Error(); }
        }
    }
}
