using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Alta.Intelligence;
using Alta.Pathfinding;
using HarmonyLib;
using Pathfinding;
using GamePath = Pathfinding.Path;

namespace PathfindingQueueDiagnostics
{
    internal static class Requests
    {
        internal sealed class Link
        {
            internal WeakReference<Navigator> Nav = null!;
            internal WeakReference<Agent> Agent = null!;
            internal WeakReference<Request>? Current;
            internal double Expires;
        }
        internal sealed class Request
        {
            internal long Id;
            internal Link Link = null!;
            internal uint Entity;
            internal int Layer, Legs, Canceled;
            internal double Started, NextPoll;
            internal string LastStage = "created";
            internal long LastTick;
            internal Leg? Preparation, Latest;
        }
        internal sealed class Leg
        {
            internal long Id;
            internal int Generation;
            internal WeakReference<GamePath> Path = null!;
            internal Request Request = null!;
        }
        internal struct Call
        {
            internal Request? Request, Previous;
            internal Leg? Leg;
            internal string? Stage;
            internal bool Scoped;
        }
        [ThreadStatic] internal static Request? Current;
        private static long nextRequest, nextLeg;
        private static readonly ConditionalWeakTable<Navigator, Link> links = new ConditionalWeakTable<Navigator, Link>();
        private static readonly List<Link> pendingLinks = new List<Link>();
        private static readonly List<Request> requests = new List<Request>();
        private static readonly GenerationCache<GamePath, Leg> paths = new GenerationCache<GamePath, Leg>(512);
        private static int cursor;
        internal static int Count => requests.Count;
        internal static long CacheEvictions => paths.Evictions;
        private static PropertyInfo pathID = null!, pipeline = null!;
        private static FieldInfo preparing = null!, seekerPath = null!, seekerID = null!;
        internal static MethodInfo Advance = null!, Return = null!;
        private static readonly Dictionary<MethodBase, string> navMethods = new Dictionary<MethodBase, string>();
        internal static void Bind()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            pathID = typeof(GamePath).GetProperty("pathID", flags) ?? throw new MissingMemberException("pathID");
            pipeline = typeof(GamePath).GetProperty("PipelineState", flags) ?? throw new MissingMemberException("PipelineState");
            if (pathID.PropertyType != typeof(ushort) || !pipeline.PropertyType.IsEnum) throw new NotSupportedException("Path generation/state changed");
            preparing = EngineState.Field(typeof(Navigator), "preparingPath");
            seekerPath = EngineState.Field(typeof(Seeker), "path"); seekerID = EngineState.Field(typeof(Seeker), "lastPathID");
            var iface = typeof(GamePath).Assembly.GetType("Pathfinding.IPathInternals", true)!;
            var map = typeof(GamePath).GetInterfaceMap(iface);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == "AdvanceState") Advance = map.TargetMethods[i];
                if (map.InterfaceMethods[i].Name == "ReturnPath") Return = map.TargetMethods[i];
            }
            if (Advance == null || Return == null) throw new MissingMethodException("Path lifecycle interface");
            navMethods[Core.Method(typeof(Navigator), "NavigateTo", typeof(UnityEngine.Vector3), typeof(float))] = "navigate";
            navMethods[Core.Method(typeof(Navigator), "Wander", typeof(UnityEngine.Vector3), typeof(int), typeof(float), typeof(OnPathDelegate))] = "wander";
        }
        internal static void Attach(HarmonyLib.Harmony h, string file)
        {
            void Add(MethodInfo method, string? before, string? after, string? postfix = null)
            {
                HarmonyMethod? Hook(string? name) => name == null ? null : new HarmonyMethod(typeof(Requests), name);
                h.Patch(method, prefix: Hook(before), finalizer: Hook(after), postfix: Hook(postfix));
                File.AppendAllText(file, ServerTrace.Row(method.DeclaringType!.FullName + "." + method.Name, "attached:request_lifecycle") + "\n");
            }
            var initialize = typeof(Agent).GetMethod("InitializeAsServer")!;
            Add(initialize, nameof(Register), null, nameof(Register));
            foreach (var method in navMethods.Keys) Add((MethodInfo)method, nameof(NavBegin), nameof(NavEnd));
            Add(Core.Method(typeof(Navigator), "CancelRequest"), null, null, nameof(Canceled));
            Add(Core.Method(typeof(AstarPath), "StartPath", typeof(GamePath), typeof(bool)), nameof(SubmitBegin), nameof(SubmitEnd));
            Add(Advance, null, nameof(Advanced));
            Add(Return, nameof(CallbackBegin), nameof(CallbackEnd));
            Add(Core.Method(typeof(GamePath), "Reset"), nameof(PoolReset), null);
            Add(Core.Method(typeof(Navigator), "PathFound", typeof(GamePath)), nameof(NavResultBegin), nameof(NavResultEnd));
            Add(Core.Method(typeof(Navigator), "OnPathFindFailed", typeof(GamePath)), nameof(NavResultBegin), nameof(NavResultEnd));
            Add(Core.Method(typeof(Navigator), "WanderTo", typeof(GamePath)), nameof(NavResultBegin), nameof(NavResultEnd));
            Add(Core.Method(typeof(Seeker), "OnPathComplete", typeof(GamePath), typeof(bool), typeof(bool)), nameof(SeekerBegin), nameof(NavResultEnd));
        }
        private static int Generation(GamePath path) => (ushort)pathID.GetValue(path, null);
        private static bool Valid(Leg leg, GamePath path) => leg.Generation == Generation(path) && paths.TryGet(path, out var current) && ReferenceEquals(current, leg);
        private static Leg? Find(GamePath path) => paths.TryGet(path, out var leg) && Valid(leg, path) ? leg : null;
        private static bool Eligible(Link link, out uint entity, out int layer)
        {
            entity = 0; layer = -1;
            if (!link.Agent.TryGetTarget(out var agent) || agent == null || agent.Entity == null || agent.Entity.PrefabRoot == null) return false;
            var root = agent.Entity.PrefabRoot;
            if (root.Chunk == null) return false;
            string chunk = root.Chunk.ChunkIdentifier;
            const string prefix = "Cave Layer ";
            if (!chunk.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(chunk.Substring(prefix.Length), out layer) || layer < CavePerformance.Core.Config.DiagnosticMinLayer) return false;
            entity = root.Identifier; return true;
        }
        private static string Pending(Request request)
        {
            if (!Core.Main) return "";
            return request.Link.Nav.TryGetTarget(out var nav) && nav != null ? nav.IsRequestingPath.ToString() : "navigator_unavailable";
        }
        private static void Emit(Request request, string stage, Leg? leg = null, GamePath? path = null, string detail = "", Exception? error = null)
        {
            if (stage != "still_pending" && !stage.StartsWith("pending_", StringComparison.Ordinal))
            {
                Volatile.Write(ref request.LastStage, stage);
                Interlocked.Exchange(ref request.LastTick, Stopwatch.GetTimestamp());
            }
            bool valid = path != null && leg != null && Valid(leg, path);
            string state = valid ? pipeline.GetValue(path, null).ToString() : "";
            string complete = valid ? path!.CompleteState.ToString() : "";
            if (valid)
            {
                var handler = path!.callback;
                string callback = handler == null ? "null" : handler.Method.DeclaringType?.FullName + "." + handler.Method.Name;
                string errorText = path.errorLog ?? "";
                if (errorText.Length > 512) errorText = errorText.Substring(0, 512);
                detail += ";callback_last=" + callback + ";error=" + path.error + ";error_log=" + errorText;
                if (path is ConstantPath cp) detail += ";constant_nodes=" + (cp.allNodes?.Count ?? -1);
            }
            detail += ";generation_valid=" + valid;
            Core.Row(Core.RequestRows, DateTime.UtcNow.ToString("O"), Core.F(Core.Now), request.Id, request.Entity, request.Layer, stage,
                leg?.Id ?? 0, leg?.Generation ?? 0, valid ? path!.GetType().Name : "", state!, complete, Core.F(Core.Now - request.Started),
                Thread.CurrentThread.ManagedThreadId, Pending(request), detail, error?.GetType().FullName ?? "", Core.RequestRows?.Dropped ?? 0);
        }
        private static Request New(Link link, string source)
        {
            if (!Eligible(link, out var entity, out var layer)) throw new InvalidOperationException("Request is outside selected cave scope");
            var request = new Request { Id = ++nextRequest, Link = link, Entity = entity, Layer = layer, Started = Core.Now, NextPoll = Core.Now + 5000, LastTick = Stopwatch.GetTimestamp() };
            if (requests.Count >= 256)
            {
                int retired = requests.FindIndex(r => r.Canceled != 0 || ReferenceEquals(r.Link, link)
                    || r.Link.Current == null || !r.Link.Current.TryGetTarget(out var current) || !ReferenceEquals(current, r));
                if (retired >= 0) requests.RemoveAt(retired);
                else Core.ScopeSkipped++;
            }
            if (requests.Count < 256) requests.Add(request);
            long previous = link.Current != null && link.Current.TryGetTarget(out var old) ? old.Id : 0;
            Emit(request, "request_observed", detail: source + ";previous_request_id=" + previous + ";callback_parent_request_id=" + (Current?.Id ?? 0));
            return request;
        }
        private static Leg BindLeg(Request request, GamePath path, string source)
        {
            var leg = new Leg { Id = ++nextLeg, Generation = Generation(path), Path = new WeakReference<GamePath>(path), Request = request };
            paths.Bind(path, leg);
            request.Legs++; request.Latest = leg;
            if (path is ConstantPath) request.Preparation = leg;
            request.Link.Current = new WeakReference<Request>(request);
            Emit(request, "path_bound", leg, path, source + ";leg_number=" + request.Legs);
            return leg;
        }
        private static void Register(Agent __instance)
        {
            if (!Core.Main || !(__instance is Turabada)) return;
            try
            {
                if (__instance.Walker == null) return;
                var nav = __instance.Walker.GetComponent<Navigator>();
                if (nav == null || links.TryGetValue(nav, out _)) return;
                var link = new Link { Nav = new WeakReference<Navigator>(nav), Agent = new WeakReference<Agent>(__instance), Expires = Core.Now + 30000 };
                links.Add(nav, link);
                if (pendingLinks.Count < 256) pendingLinks.Add(link); else Core.ScopeSkipped++;
            }
            catch { Core.Error(); }
        }
        private static void NavBegin(Navigator __instance, MethodBase __originalMethod, out Call __state)
        {
            __state = default;
            if (!Core.Main) return;
            try
            {
                if (!links.TryGetValue(__instance, out var link) || !Eligible(link, out _, out _)) return;
                var request = New(link, "navigator_" + navMethods[__originalMethod]);
                __state = new Call { Request = request, Previous = Current, Scoped = true };
                Current = request;
            }
            catch { Core.Error(); }
        }
        private static void NavEnd(Call __state, Exception? __exception)
        {
            if (!__state.Scoped) return;
            try
            {
                if (__state.Request!.Legs == 0) __state.Request.Link.Current = new WeakReference<Request>(__state.Request);
                Emit(__state.Request, "navigator_call_return", detail: "submitted_legs=" + __state.Request.Legs, error: __exception);
            }
            catch { Core.Error(); }
            finally { Current = __state.Previous; }
        }
        private static void Canceled(Navigator __instance)
        {
            if (!Core.Main) return;
            try
            {
                if (links.TryGetValue(__instance, out var link) && link.Current != null && link.Current.TryGetTarget(out var request))
                { Interlocked.Exchange(ref request.Canceled, 1); Emit(request, "navigator_cancel_call"); }
            }
            catch { Core.Error(); }
        }
        private static void SubmitBegin(GamePath __0, out Call __state)
        {
            __state = default;
            if (!Core.Main || Current == null) return;
            try
            {
                var leg = BindLeg(Current, __0, "astar_submit");
                __state = new Call { Request = Current, Leg = leg };
                Emit(Current, "submit_begin", leg, __0);
            }
            catch { Core.Error(); }
        }
        private static void SubmitEnd(GamePath __0, Call __state, Exception? __exception)
        {
            if (__state.Request == null) return;
            try { Emit(__state.Request, "submit_return", __state.Leg, __0, error: __exception); } catch { Core.Error(); }
        }
        private static void Advanced(GamePath __instance, object __0, Exception? __exception)
        {
            if (!Core.Ready || Core.DisableRequested) return;
            try
            {
                var leg = Find(__instance);
                if (leg != null) Emit(leg.Request, "pipeline_advance", leg, __instance, "requested_state=" + __0, __exception);
            }
            catch { Core.Error(); }
        }
        private static void PoolReset(GamePath __instance)
        {
            if (!Core.Ready || Core.DisableRequested) return;
            try
            {
                var leg = Find(__instance);
                if (leg != null) Emit(leg.Request, "path_pool_reset", leg, __instance, "old_binding_removed_before_original_reset");
                paths.Invalidate(__instance);
            }
            catch { Core.Error(); }
        }
        private static void CallbackBegin(GamePath __instance, out Call __state)
        {
            __state = default;
            if (!Core.Ready || Core.DisableRequested) return;
            try
            {
                var leg = Find(__instance); if (leg == null) return;
                __state = new Call { Request = leg.Request, Leg = leg, Previous = Current, Scoped = true };
                Current = leg.Request;
                Emit(leg.Request, "callback_begin", leg, __instance);
            }
            catch { Core.Error(); }
        }
        private static void CallbackEnd(GamePath __instance, Call __state, Exception? __exception)
        {
            if (!__state.Scoped) return;
            try { Emit(__state.Request!, "callback_return", __state.Leg, __instance, error: __exception); }
            catch { Core.Error(); }
            finally { Current = __state.Previous; }
        }
        private static void NavResultBegin(Navigator __instance, GamePath __0, MethodBase __originalMethod, out Call __state)
        {
            __state = default;
            if (!Core.Main) return;
            try
            {
                var leg = Find(__0); if (leg == null) return;
                __state = new Call { Request = leg.Request, Leg = leg, Previous = Current, Scoped = true, Stage = __originalMethod.Name };
                Current = leg.Request;
                Emit(leg.Request, __originalMethod.Name + "_begin", leg, __0);
            }
            catch { Core.Error(); }
        }
        private static void SeekerBegin(Seeker __instance, GamePath __0, bool __1, bool __2, out Call __state)
        {
            __state = default;
            if (!Core.Main) return;
            try
            {
                var leg = Find(__0); if (leg == null) return;
                __state = new Call { Request = leg.Request, Leg = leg, Previous = Current, Scoped = true, Stage = "seeker_complete" };
                Current = leg.Request;
                Emit(leg.Request, "seeker_complete_begin", leg, __0,
                    "seeker_current_same=" + ReferenceEquals(seekerPath.GetValue(__instance), __0) + ";seeker_last_path_id=" + seekerID.GetValue(__instance) + ";modifiers=" + __1 + ";send_callbacks=" + __2);
            }
            catch { Core.Error(); }
        }
        private static void NavResultEnd(GamePath __0, Call __state, Exception? __exception)
        {
            if (!__state.Scoped) return;
            try { Emit(__state.Request!, __state.Stage + "_return", __state.Leg, __0, error: __exception); }
            catch { Core.Error(); }
            finally { Current = __state.Previous; }
        }
        internal static void Poll()
        {
            if (!Core.Main) return;
            double start = Core.Now;
            for (int i = 0; i < 4 && pendingLinks.Count > 0 && Core.Now - start < 2; i++)
            {
                var link = pendingLinks[0]; pendingLinks.RemoveAt(0);
                if (Core.Now >= link.Expires || !link.Nav.TryGetTarget(out var nav) || nav == null) continue;
                if (!Eligible(link, out _, out _)) { pendingLinks.Add(link); continue; }
                if (nav.IsRequestingPath && (link.Current == null || !link.Current.TryGetTarget(out _)))
                {
                    var r = New(link, "already_pending;submission_history_unknown");
                    link.Current = new WeakReference<Request>(r);
                    if (preparing.GetValue(nav) is GamePath p) BindLeg(r, p, "late_field_reference;historical_generation_unknown");
                    if (nav.Seeker != null && seekerPath.GetValue(nav.Seeker) is GamePath s && Generation(s) == Convert.ToInt32(seekerID.GetValue(nav.Seeker)))
                        BindLeg(r, s, "late_seeker_reference;submission_history_unknown");
                }
            }
            int visited = 0, initial = requests.Count, emitted = 0;
            while (requests.Count > 0 && visited++ < initial && emitted < 8 && Core.Now - start < 2)
            {
                if (cursor >= requests.Count) cursor = 0;
                var request = requests[cursor];
                if (Core.Now - request.Started > 120000 || !request.Link.Nav.TryGetTarget(out var nav) || nav == null)
                { requests.RemoveAt(cursor); continue; }
                bool current = request.Link.Current != null && request.Link.Current.TryGetTarget(out var live) && ReferenceEquals(live, request);
                if (request.Canceled != 0 || !current || !nav.IsRequestingPath)
                { requests.RemoveAt(cursor); continue; }
                cursor++;
                if (Core.Now < request.NextPoll) continue;
                request.NextPoll = Core.Now + 5000;
                string common = "last_event=" + Volatile.Read(ref request.LastStage) + ";last_event_age_ms=" + Core.F((Stopwatch.GetTimestamp() - Interlocked.Read(ref request.LastTick)) * 1000.0 / Stopwatch.Frequency)
                    + ";game_request_age_s=" + Core.F(UnityEngine.Time.time - nav.LastPathRequest) + ";finished=" + nav.IsFinished + ";legs=" + request.Legs;
                EmitLegSnapshot(request, request.Preparation, common + ";role=preparation");
                if (!ReferenceEquals(request.Latest, request.Preparation)) EmitLegSnapshot(request, request.Latest, common + ";role=latest");
                if (request.Latest == null) Emit(request, "pending_no_path_observed", detail: common);
                EngineState.Emit("pending_request", request.Id, Span.Current?.Id ?? 0);
                emitted++;
            }
        }
        private static void EmitLegSnapshot(Request request, Leg? leg, string detail)
        {
            if (leg == null) return;
            if (!leg.Path.TryGetTarget(out var path)) { Emit(request, "pending_path_collected", leg, detail: detail); return; }
            if (!Valid(leg, path))
            { Emit(request, leg.Generation != Generation(path) ? "pending_path_recycled" : "pending_path_binding_evicted", leg, detail: detail); return; }
            Emit(request, "still_pending", leg, path, detail);
        }
        internal static void Clear() { requests.Clear(); pendingLinks.Clear(); Current = null; }
    }
}
