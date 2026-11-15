using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using CavePerformance;
using HarmonyLib;
using Pathfinding;
using UnityEngine;
using GamePath = Pathfinding.Path;
using Path = System.IO.Path;

internal sealed class FakeGraph : NavGraph
{
    internal GraphNode? Node;
    internal int Calls;
    internal bool Throw;
    public override void GetNodes(Action<GraphNode> action) { if (Node != null) action(Node); }
    public override IEnumerable<Progress> ScanInternal() { yield break; }
    public override NNInfoInternal GetNearest(Vector3 p, NNConstraint c, GraphNode hint) => GetNearestForce(p,c);
    public override NNInfoInternal GetNearestForce(Vector3 p, NNConstraint c)
    {
        Interlocked.Increment(ref Calls);
        if (Throw) throw new InvalidOperationException("native graph fixture failure");
        if (Node == null || (c != null && !c.Suitable(Node))) return default;
        return new NNInfoInternal(Node) { clampedPosition = (Vector3)Node.position, constrainedNode = Node, constClampedPosition = (Vector3)Node.position };
    }
}
internal static class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static AstarPath Astar()
    {
        var astar=(AstarPath)FormatterServices.GetUninitializedObject(typeof(AstarPath));
        astar.data=(AstarData)FormatterServices.GetUninitializedObject(typeof(AstarData));return astar;
    }
    private static FakeGraph Graph(uint index, float y)
    {
        var node = (PointNode)FormatterServices.GetUninitializedObject(typeof(PointNode));
        node.Walkable=true; node.Area=index+1; node.GraphIndex=index; node.position=(Int3)new Vector3(0,y,0);
        return new FakeGraph { graphIndex=index, Node=node };
    }
    private static NavGraph[] Slots(params FakeGraph[] graphs)
    {
        var slots=new NavGraph[102];foreach(var graph in graphs)slots[graph.graphIndex]=graph;return slots;
    }
    private static ConstantPath PathFor(ushort id, Vector3 position)
    {
        var path = new ConstantPath { startPoint=position, originalStartPoint=position, nnConstraint=NNConstraint.Default };
        path.nnConstraint.constrainDistance=false;
        Search.PathId.SetValue(path,id,null);return path;
    }
    private static void SettingsTests()
    {
        var settings=Settings.Parse(Settings.Defaults);
        Check(settings.Workers==2 && settings.LocalSearch && settings.SeparateLoads && settings.GenerationMs==4 && settings.NavigationRecovery && settings.StepDiagnostics && settings.QueueDiagnostics && settings.MobDiagnostics,"defaults");
        var legacy=Settings.Parse("enabled=true\npath_workers=1\ncave_wyrm_multiplier=0.75");
        Check(legacy.DiagnosticMinLayer==90 && Settings.Parse("cave_diagnostic_min_layer=0").DiagnosticMinLayer==0,"Configurable recorder floor lost");
        Check(legacy.NavigationRecovery && legacy.StepDiagnostics && legacy.QueueDiagnostics && legacy.MobDiagnostics,"Legacy config failed to retain embedded components");
        var quiet=Settings.Parse("navigation_recovery=false\ncave_step_diagnostics=false\npathfinding_queue_diagnostics=false\nmob_combat_diagnostics=false");
        Check(!quiet.NavigationRecovery && !quiet.StepDiagnostics && !quiet.QueueDiagnostics && !quiet.MobDiagnostics,"Component switches ignored");
        foreach(string invalid in new[]{"path_workers=8","path_workers=-1","generation_ms=NaN","generation_ms=17","unknown=true","enabled=maybe","cave_diagnostic_min_layer=-1"})
        {
            bool threw=false;try{Settings.Parse(invalid);}catch(FormatException){threw=true;}
            Check(threw,"Invalid configuration accepted: "+invalid);
        }
        var astar=Astar();astar.threadCount=ThreadCount.One;Search.Workers(astar);
        Check(astar.threadCount==ThreadCount.Two,"Two-worker startup setting not applied");
        Core.Config.Workers=0;astar.threadCount=ThreadCount.Three;Search.Workers(astar);
        Check(astar.threadCount==ThreadCount.Three,"Native startup setting overridden");Core.Config.Workers=2;
        Check(Loading.ClampBudget(1,80)==4 && Loading.ClampBudget(1,.5f)==1 && Loading.ClampBudget(1,2)==2,"render cap increased or lost native allowance");
        Core.Config.GenerationMs=0;Check(Loading.ClampBudget(1,80)==80,"Disabled cap changed native budget");Core.Config.GenerationMs=4;
        bool rejected=false;try{Loading.CapRenderBudget(new[]{new CodeInstruction(OpCodes.Ret)}).ToArray();}catch(NotSupportedException){rejected=true;}
        Check(rejected,"Changed render IL was accepted");
        var bounds=new Bounds(new Vector3(0,-1500,0),new Vector3(400,15,400));
        Check(Search.Inside(bounds,new Vector3(1,-1500,1))&&!Search.Inside(bounds,new Vector3(1,-1515,1)),"Cross-layer target passed local bounds guard");
        Console.WriteLine("PASS settings: bounded startup controls, native/two-worker selection, render cap preserving smaller native budgets, malformed IL rejection.");
    }
    private static void SearchTests()
    {
        var h=new HarmonyLib.Harmony("ATT.CavePerformance.SearchFixture");
        var astar=Astar();var world=Graph(0,0);var cave=Graph(100,-1500);var neighbor=Graph(101,-1515);
        astar.data.graphs=Slots(world,cave,neighbor);AstarPath.active=astar;
        var position=new Vector3(0,-1500,0);var path=PathFor(7,position);
        try
        {
            h.Patch(Search.Nearest,prefix:new HarmonyMethod(typeof(Search),"QueryBegin"),postfix:new HarmonyMethod(typeof(Search),"QueryResult"),finalizer:new HarmonyMethod(typeof(Search),"QueryEnd"));
            h.Patch(Search.Suitable,prefix:new HarmonyMethod(typeof(Search),"Filter"));
            h.Patch(Search.ConstantPrepare,prefix:new HarmonyMethod(typeof(Search),"PrepareBegin"),finalizer:new HarmonyMethod(typeof(Search),"PrepareEnd"));
            var original=astar.GetNearest(position,path.nnConstraint,null);
            Check(original.node==cave.Node && world.Calls>0 && neighbor.Calls>0,"Unscoped original graph search changed");
            Search.MarkScanned(cave);Search.BindPath(path,new Search.Binding { DirectGraph=cave,Layer=100 });
            world.Calls=cave.Calls=neighbor.Calls=0;
            Search.ConstantPrepare.Invoke(path,null);
            Check(path.startNode==cave.Node && cave.Calls==1 && world.Calls==0 && neighbor.Calls==0,"Local preparation did not isolate graph index 100 by identity");
            Check(Search.Preparation==null && Search.CurrentQuery==null,"Successful preparation leaked thread state");

            // Early agent requests can be queued before their cave graph exists.
            // Resolving ownership later must still wait for a completed scan.
            var owner=(Alta.Chunks.CaveLayerStreamer)FormatterServices.GetUninitializedObject(typeof(Alta.Chunks.CaveLayerStreamer));
            var lateGraph=(RecastGraph)FormatterServices.GetUninitializedObject(typeof(RecastGraph));lateGraph.graphIndex=100;
            var late=PathFor(77,position);Search.BindPath(late,new Search.Binding{Cave=new WeakReference<Alta.Chunks.CaveLayerStreamer>(owner)});
            long before=Search.ScopedQueries;Search.PrepareBegin(late,out var lateState);astar.GetNearest(position,late.nnConstraint,null);Search.PrepareEnd(lateState,null);
            Check(Search.ScopedQueries==before,"Missing cave graph was scoped");
            typeof(Alta.Chunks.CaveLayerStreamer).GetField("navGraph",Instance)!.SetValue(owner,lateGraph);
            astar.data.graphs[100]=lateGraph;
            Search.PrepareBegin(late,out lateState);astar.GetNearest(position,late.nnConstraint,null);Search.PrepareEnd(lateState,null);
            Check(Search.ScopedQueries==before,"Incomplete graph was scoped");
            Search.MarkScanned(lateGraph);Search.PrepareBegin(late,out lateState);astar.GetNearest(position,late.nnConstraint,null);Search.PrepareEnd(lateState,null);
            Check(Search.ScopedQueries==before+1,"Deferred graph did not become eligible after completed scan");
            astar.data.graphs=Slots(world,cave,neighbor);

            // A missing local result repeats the unmodified full search.
            var saved=cave.Node;cave.Node=null;world.Calls=neighbor.Calls=0;
            Search.PrepareBegin(path,out var state);
            var fallback=astar.GetNearest(position,path.nnConstraint,null);
            Search.PrepareEnd(state,null);
            Check(fallback.node==neighbor.Node && world.Calls>0 && neighbor.Calls>0 && Search.Fallbacks>0,"Missing local node did not fall back globally");
            cave.Node=saved;

            // Constraints retain native walkability/tags/area and graph masks.
            var restricted=NNConstraint.Default;restricted.constrainDistance=false;restricted.graphMask=1;
            Search.PrepareBegin(path,out state);var nativeMask=astar.GetNearest(position,restricted,null);Search.PrepareEnd(state,null);
            Check(nativeMask.node==world.Node,"Explicit native graph mask was bypassed");
            var pn=PathNNConstraint.Default;pn.constrainDistance=false;pn.SetStart(cave.Node);
            Search.PrepareBegin(path,out state);var area=astar.GetNearest(position,pn,null);Search.PrepareEnd(state,null);
            Check(area.node==cave.Node && pn.constrainArea,"Native PathNNConstraint.SetStart/area behavior changed");

            Search.PathId.SetValue(path,(ushort)8,null);world.Calls=0;
            Search.ConstantPrepare.Invoke(path,null);
            Check(world.Calls>0,"Recycled generation used old cave binding");
            Search.BindPath(path,new Search.Binding { DirectGraph=cave,Layer=100 });
            astar.data.graphs=Slots(world,neighbor);world.Calls=0;
            Search.ConstantPrepare.Invoke(path,null);Check(world.Calls>0,"Removed graph binding did not use native search");
            astar.data.graphs=Slots(world,cave,neighbor);

            // Exercise original graph exceptions under the real native lookup.
            cave.Throw=true;Search.PrepareBegin(path,out state);
            bool threw=false;try{astar.GetNearest(position,path.nnConstraint,null);}catch(InvalidOperationException){threw=true;}
            finally{Search.PrepareEnd(state,null);cave.Throw=false;}
            Check(threw && Search.CurrentQuery==null && Search.Preparation==null,"Exception changed or scope leaked");

            // Concurrent worker scopes must not select each other's graph.
            var a=PathFor(9,position);var b=PathFor(10,new Vector3(0,-1515,0));
            Search.MarkScanned(neighbor);Search.BindPath(a,new Search.Binding{DirectGraph=cave});Search.BindPath(b,new Search.Binding{DirectGraph=neighbor});
            Exception? error=null;
            var barrier=new Barrier(2);
            void Run(ConstantPath p,GraphNode expected)
            {
                try{for(int i=0;i<20;i++){Search.PrepareBegin(p,out var s);Check(barrier.SignalAndWait(5000),"Worker rendezvous timed out");var result=astar.GetNearest(p.startPoint,p.nnConstraint,null);Search.PrepareEnd(s,null);Check(result.node==expected,"Worker graph contexts mixed");}}
                catch(Exception e){error=e;}
            }
            var t1=new Thread(()=>Run(a,cave.Node!)){IsBackground=true};var t2=new Thread(()=>Run(b,neighbor.Node!)){IsBackground=true};t1.Start();t2.Start();
            Check(t1.Join(10000)&&t2.Join(10000),"Worker scope test deadlocked");if(error!=null)throw error;
            h.UnpatchSelf();world.Calls=0;astar.GetNearest(position,path.nnConstraint,null);Check(world.Calls>0,"Unpatch did not restore native search");
            Console.WriteLine("PASS actual native nearest search/ConstantPath.Prepare: graph 100 identity scope, unchanged unbound calls, full-search fallback, native mask/area constraints, pooled generation, graph removal, exception restoration, two concurrent workers and rollback.");
        }
        finally{h.UnpatchSelf();AstarPath.active=null;Search.CurrentQuery=null;Search.Preparation=null;}
    }
    private static async Task SimulateNativeLoader(Task dynamic)
    {
        await Loading.AfterDynamic(dynamic);
        events.Add("entities");events.Add("dispose_save");events.Add("ready");
    }
    private static readonly List<string> events=new List<string>();
    private static void LoadingTests()
    {
        Loading.Frame=()=>100;events.Clear();
        var original=new TaskCompletionSource<bool>();var loader=SimulateNativeLoader(original.Task);
        Check(!loader.IsCompleted && Loading.Pending==0,"Gate inserted before native dynamic generation completed");
        original.SetResult(true);Check(!loader.IsCompleted&&events.Count==0&&Loading.Pending==1,"Native save completed before frame gate");
        Loading.Tick(100);Check(!loader.IsCompleted,"Gate released within the generation frame");
        Loading.Tick(101);loader.GetAwaiter().GetResult();Check(string.Join(",",events)=="entities,dispose_save,ready","Native continuation ordering changed");
        var a=Loading.NextFrame(102);var b=Loading.NextFrame(102);Loading.Tick(103);
        Check(a.IsCompleted&&!b.IsCompleted,"Multiple native entity batches released in one frame");Loading.Tick(103);Check(!b.IsCompleted,"Repeated tick released another same-frame batch");
        Loading.Tick(104);Check(b.IsCompleted,"Second native batch not released on next frame");
        var marker=new InvalidOperationException("dynamic generation failed");
        var failed=Loading.AfterDynamic(Task.FromException(marker));Exception? actual=null;
        try{failed.GetAwaiter().GetResult();}catch(Exception e){actual=e;}
        Check(ReferenceEquals(marker,actual)&&Loading.Pending==0,"Native dynamic failure changed or queued work");
        Core.Config.SeparateLoads=false;Loading.AfterDynamic(Task.CompletedTask).GetAwaiter().GetResult();Check(Loading.Pending==0,"Disabled separation queued work");Core.Config.SeparateLoads=true;
        var gates=new List<Task>();for(int i=0;i<40;i++)gates.Add(Loading.NextFrame(200));
        Check(Loading.Pending==32&&gates.Skip(32).All(t=>t.IsCompleted),"Load gate cap did not fall back without losing native work");
        Loading.Drain();Check(gates.All(t=>t.IsCompleted)&&Loading.Pending==0,"Shutdown abandoned native loaders");
        var beforeSave=Loading.NextFrame(250);
        Loading.BeginSynchronous(false,out var ordinarySave);
        Check(!ordinarySave&&!beforeSave.IsCompleted,"Ordinary async save drained gates");
        Loading.BeginSynchronous(true,out var outerSave);
        Check(outerSave&&beforeSave.IsCompleted&&Loading.NextFrame(250).IsCompleted,"Instant save could wait on a future-frame gate");
        Loading.BeginSynchronous(true,out var nestedSave);Loading.SaveEnd(nestedSave);
        Check(Loading.NextFrame(250).IsCompleted,"Nested save released outer synchronous scope");
        Loading.SaveEnd(outerSave);
        var afterSave=Loading.NextFrame(250);Check(!afterSave.IsCompleted,"Instant save scope leaked");Loading.Drain();
        var wrap=Loading.NextFrame(int.MaxValue);Loading.Tick(int.MinValue);Check(wrap.IsCompleted,"Frame counter rollover stranded a load");
        Loading.Frame=()=>300;
        Check(Loading.RenderBegin(null!,out var render),"Fresh frame rejected generation");Thread.Sleep(6);Loading.RenderEnd(render);
        var owner=(Alta.Caves.GenerationBase)FormatterServices.GetUninitializedObject(typeof(Alta.Caves.GenerationBase));
        Alta.Caves.GenerationBase.CurrentlyRendering=owner;
        Check(!Loading.RenderBegin(owner,out render),"Second generator ignored shared frame budget");
        Check(ReferenceEquals(Alta.Caves.GenerationBase.CurrentlyRendering,null),"Deferred generator retained scheduler ownership");
        Loading.Frame=()=>301;Check(Loading.RenderBegin(null!,out render),"New frame did not resume generation");Loading.RenderEnd(render);
        Console.WriteLine("PASS load scheduler: successful native work precedes gate; entity/save/ready continuation waits; one release per distinct frame; exceptions preserved; disabled/cap fallback; shutdown drain; frame-counter rollover. Entity restore itself is atomic.");
    }
    private static void Attachments()
    {
        var h=new HarmonyLib.Harmony("ATT.CavePerformance.FullAttachmentFixture");
        var other=new HarmonyLib.Harmony("ATT.CavePerformance.IndependentObserver");
        try
        {
            other.Patch(Loading.Render,postfix:new HarmonyMethod(typeof(Program),nameof(Observer)));
            Core.Attach(h);
            Check(HarmonyLib.Harmony.GetAllPatchedMethods().Count(m=>HarmonyLib.Harmony.GetPatchInfo(m).Owners.Contains(h.Id))>=16,"Required hooks missing");
            h.UnpatchSelf();Check(HarmonyLib.Harmony.GetPatchInfo(Loading.Render).Owners.Contains(other.Id),"Rollback removed unrelated patch owner");
            Console.WriteLine("PASS actual installed method attachment: worker initialization, scan lifecycle, cave wander/submit/reset, both path preparations, nearest graph filtering/fallback, generation cap, dynamic-load task, entity context, transition and coroutine timing; independent-owner rollback.");
        }
        finally{h.UnpatchSelf();other.UnpatchSelf();}
    }
    private static void Observer() { }
    private static void ExistingMods()
    {
        string game=Environment.GetEnvironmentVariable("ATT_GAME_PATH")!;
        string temp=Path.Combine(Path.GetTempPath(),"caveperf-hooks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        var root=typeof(AstarPath).Assembly;
        Assembly Mod(string file) => file == "ForestNavigationGuard"
            ? Assembly.LoadFrom(Environment.GetEnvironmentVariable("ATT_FOREST_PERFORMANCE_DLL")!)
            : new[] { "CaveNavigationRecovery", "CaveStepDiagnostics", "PathfindingQueueDiagnostics", "MobCombatDiagnostics" }.Contains(file)
                ? typeof(Core).Assembly : Assembly.LoadFrom(Path.Combine(game,"Mods",file+".dll"));
        void Invoke(Type type,string name,params object[] args)=>type.GetMethod(name,Static)!.Invoke(null,args);
        var cave=Mod("CaveNavigationRecovery").GetType("CaveNavigationRecovery.Core",true)!;
        var gc=Mod("DailyGarbageCollection").GetType("DailyGarbageCollection.Patch",true)!;
        var forest=Mod("ForestNavigationGuard");var selection=forest.GetType("ForestNavigationGuard.Patch",true)!;
        var rebuilt=forest.GetType("ForestNavigationGuard.RebuiltTiles",true)!;
        var queue=Mod("PathfindingQueueDiagnostics");var request=queue.GetType("PathfindingQueueDiagnostics.Requests",true)!;
        var flush=queue.GetType("PathfindingQueueDiagnostics.Flush",true)!;
        var step=Mod("CaveStepDiagnostics").GetType("CaveStepDiagnostics.PathProbe",true)!;
        var mob=typeof(Core).Assembly.GetType("MobCombatDiagnostics.Hooks",true)!;
        void AttachOther(HarmonyLib.Harmony h)
        {
            Invoke(cave,"Bind",root);Invoke(cave,"Attach",h);
            var pair=gc.GetMethod("Resolve",Static)!.Invoke(null,new object[]{root})!;
            var fields=pair.GetType().GetFields();
            h.Patch((MethodInfo)fields.Single(f=>f.Name=="Item1").GetValue(pair),prefix:new HarmonyMethod(gc,"SkipGameTimer"));
            h.Patch((MethodInfo)fields.Single(f=>f.Name=="Item2").GetValue(pair),transpiler:new HarmonyMethod(gc,"ReplaceScanCollect"));
            var suitable=(MethodInfo)selection.GetMethod("Resolve",Static)!.Invoke(null,new object[]{root})!;
            h.Patch(suitable,prefix:new HarmonyMethod(selection,"Prefix"));
            var post=(MethodInfo)rebuilt.GetMethod("Resolve",Static)!.Invoke(null,new object[]{root})!;
            h.Patch(post,transpiler:new HarmonyMethod(rebuilt,"ReplaceNotificationArgument"));
            var forestLoading=forest.GetType("ForestPerformance.Loading",true)!;
            var connectivity=forest.GetType("ForestPerformance.Connectivity",true)!;
            Invoke(forestLoading,"Bind");Invoke(forestLoading,"Attach",h);
            Invoke(connectivity,"Bind");Invoke(connectivity,"Attach",h);
            Invoke(queue.GetType("PathfindingQueueDiagnostics.EngineState",true)!,"Bind");
            Invoke(request,"Bind");Invoke(flush,"Bind");
            Invoke(request,"Attach",h,Path.Combine(temp,"requests.csv"));Invoke(flush,"Attach",h,Path.Combine(temp,"flush.csv"));
            object targets=step.GetField("Targets",Static)!.GetValue(null)!;targets.GetType().GetMethod("Clear")!.Invoke(targets,null);
            Invoke(step,"AttachAll",root,h,Path.Combine(temp,"steps.csv"));
            Invoke(mob,"Validate"); Invoke(mob,"Attach",h,Path.Combine(temp,"combat.csv"));
        }
        void Probe()
        {
            var astar=Astar();var world=Graph(0,0);var own=Graph(100,-1500);astar.data.graphs=Slots(world,own);AstarPath.active=astar;
            var p=PathFor(42,new Vector3(0,-1500,0));Search.MarkScanned(own);Search.BindPath(p,new Search.Binding{DirectGraph=own});
            Search.ConstantPrepare.Invoke(p,null);Check(p.startNode==own.Node&&world.Calls==0,"Other installed hooks changed local selection");
        }
        try
        {
            for(int order=0;order<2;order++)
            {
                var ours=new HarmonyLib.Harmony("ATT.CavePerformance.CompositionFixture");
                var other=new HarmonyLib.Harmony("ATT.CavePerformance.InstalledOwnersFixture");
                try
                {
                    if(order==0){Core.Attach(ours);AttachOther(other);}else{AttachOther(other);Core.Attach(ours);}
                    Probe();ours.UnpatchSelf();
                    Check(HarmonyLib.Harmony.GetPatchInfo(Search.Suitable).Owners.Contains(other.Id),"Our unpatch removed installed owner");
                    Core.Attach(ours);other.UnpatchSelf();
                    Check(HarmonyLib.Harmony.GetPatchInfo(Search.Suitable).Owners.Contains(ours.Id),"Installed owner rollback removed our patch");
                }
                finally{ours.UnpatchSelf();other.UnpatchSelf();AstarPath.active=null;}
            }
            Console.WriteLine("PASS installed-mod composition in both attachment orders: consolidated cave recovery/steps/queue components, DailyGarbageCollection scan transpiler/timer guard, Forest Performance graph/tile components; local graph choice and independent rollback preserved.");
        }
        finally{Directory.Delete(temp,true);}
    }
    private static void TimingTests()
    {
        var h=new HarmonyLib.Harmony("ATT.CavePerformance.TimingFixture");
        string temp=Path.Combine(Path.GetTempPath(),"caveperf-trace-"+Guid.NewGuid().ToString("N"));
        string file=Path.Combine(temp,"timing.csv");
        Core.Trace=new ServerTrace(file,"utc,mono_ms,stage,duration_ms,thread_id,detail,exception_type,dropped_rows");Core.Transition();
        try
        {
            Core.Attach(h);
            Check(Timing.WorkInvoke!=null,"Unity continuation profiler did not bind");
            int count=0;
            using(var signal=new ManualResetEvent(false))
            {
                SendOrPostCallback callback=state=>{count++;Thread.Sleep(30);};
                var work=Activator.CreateInstance(Timing.WorkInvoke!.DeclaringType!,Instance,null,new object[]{callback,"fixture",signal},null);
                Timing.WorkInvoke.Invoke(work,null);
                Check(count==1&&signal.WaitOne(0),"Profiling changed original Unity callback or wait-handle signal");
            }
            var setup=typeof(Application).Assembly.GetType("UnityEngine.SetupCoroutine",true)!;
            var invoke=setup.GetMethod("InvokeMoveNext",Static)!;
            var pointer=Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(pointer,0);
                var iterator=SlowStep().GetEnumerator();invoke.Invoke(null,new object[]{iterator,pointer});
                Check(Marshal.ReadByte(pointer)==1,"Coroutine original yielded result changed");
                var marker=new InvalidOperationException("iterator fixture failure");
                Exception? caught=null;try{invoke.Invoke(null,new object[]{new ThrowStep(marker),pointer});}catch(TargetInvocationException e){caught=e.InnerException;}
                Check(ReferenceEquals(caught,marker),"Coroutine original exception was changed or swallowed");
            }
            finally{Marshal.FreeHGlobal(pointer);}
        }
        finally{h.UnpatchSelf();Core.Trace.Dispose();Core.Trace=null;}
        string rows=File.ReadAllText(file);
        Check(rows.Contains("unity_task_callback")&&rows.Contains("unity_coroutine="),"Slow continuation/coroutine timings missing");
        Check(rows.Contains("InvalidOperationException"),"Coroutine error observation missing");Directory.Delete(temp,true);
        Console.WriteLine("PASS real Unity managed task/coroutine boundaries: callback exactly once, wait signal retained, iterator result and exception preserved, bounded asynchronous timing output observed.");
    }
    private static IEnumerable SlowStep(){Thread.Sleep(30);yield return null;}
    private sealed class ThrowStep:IEnumerator
    {
        private readonly Exception error;internal ThrowStep(Exception e){error=e;}
        public object? Current=>null;public bool MoveNext()=>throw error;public void Reset()=>throw new NotSupportedException();
    }
    private static void Main()
    {
        Core.MainThread=Thread.CurrentThread.ManagedThreadId;Core.Config=new Settings();Core.Bind();Core.Ready=true;
        SettingsTests();SearchTests();LoadingTests();Attachments();ExistingMods();TimingTests();WyrmTests.Run();SearchBindingTests.Run();ScanPacingTests.Run();GateContextTests.Run();ConsolidationTests.Run();
        Core.Ready=false;
        Console.WriteLine("LIMIT: host fixtures do not execute live Unity scene loading, networking, mesh cutting or player movement; startup deployment still requires live validation at a normal restart.");
    }
}
