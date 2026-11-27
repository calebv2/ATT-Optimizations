using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Alta.Chunks;
using Alta.Networking;
using Alta.Populations;
using CavePerformance;
using Creature = CavePerformance.CavePopulationTargets.Creature;
using HarmonyLib;
using UnityEngine;

internal static class WyrmTests
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
    private static int spawned;
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static T Object<T>() where T:UnityEngine.Object
    {
        // Only managed fields/getters execute on these fixture objects. No fake
        // native pointer is dereferenced or passed to an engine call.
        var value=(T)FormatterServices.GetUninitializedObject(typeof(T));
        typeof(UnityEngine.Object).GetField("m_CachedPtr",Flags)!.SetValue(value,new IntPtr(1));return value;
    }
    private static void Field(object instance,string name,object? value)
    {
        for(Type? type=instance.GetType();type!=null;type=type.BaseType)
        {
            var field=type.GetField(name,Flags|BindingFlags.DeclaredOnly);if(field!=null){field.SetValue(instance,value);return;}
        }
        throw new MissingFieldException(instance.GetType().FullName,name);
    }
    private static PopulationSample Sample(int layer,bool cave=true,bool mixed=false,int existing=0,Creature kind=Creature.Wyrm,bool oneOff=false)
    {
        var chunk=Object<LocationChunk>();
        if(cave){var streamer=Object<CaveLayerStreamer>();Field(streamer,"layer",layer);Field(chunk,"<ProceduralStreamer>k__BackingField",streamer);}
        var def=Object<PopulationDefinition>();Field(def,"percentageOfPopulationSpawned",1f);
        var distribution=Object<Alta.Caves.Distribution>();
        var a=new Alta.Caves.Distribution.Item();var b=new Alta.Caves.Distribution.Item();
        var first=Object<NetworkPrefab>();var second=Object<NetworkPrefab>();Field(a,"topic",first);Field(b,"topic",second);
        Field(distribution,"items",new List<Alta.Caves.Distribution.Item>{a,b});
        Field(distribution,"festivities",new List<Alta.Caves.Distribution.FestivityGroup>());
        Field(def,"distribution",distribution);
        CavePopulationTargets.ClassifyPrefab=p=>ReferenceEquals(p,first)?kind:
            ReferenceEquals(p,second)?(mixed?(kind==Creature.Wyrm?Creature.Turabada:Creature.Wyrm):kind):Creature.Other;
        var sample=new PopulationSample(def,8,8,(PopulationFolderBase)null!,oneOff);Field(sample,"<Chunk>k__BackingField",chunk);
        Field(sample,"spawner",new SpawnSystem());
        var entities=new Dictionary<NetworkEntity,PopulationItem>(new ReferenceComparer());
        for(int i=0;i<existing;i++)entities.Add(Object<NetworkEntity>(),null!);
        Field(sample,"<SpawnedEntities>k__BackingField",entities);return sample;
    }
    private sealed class ReferenceComparer:IEqualityComparer<NetworkEntity>
    {
        public bool Equals(NetworkEntity a,NetworkEntity b)=>ReferenceEquals(a,b);
        public int GetHashCode(NetworkEntity v)=>RuntimeHelpers.GetHashCode(v);
    }
    private sealed class SpawnSystem:ISpawnSystem
    {
        public NetworkEntity Entity=>null!;
        public Transform transform=>null!;
        public PopulationSpawnInformation this[uint hash]=>null!;
    }
    private static bool FakeSpawn(ref bool __result){spawned++;__result=true;return false;}
    private static bool SkipAnalytics()=>false;
    private static int Execute(PopulationSample sample){spawned=0;CavePopulationTargets.Check.Invoke(sample,null);return spawned;}
    internal static void Run()
    {
        var native=new HarmonyLib.Harmony("ATT.CavePerformance.WyrmNativeFixture");
        var patch=new HarmonyLib.Harmony("ATT.CavePerformance.WyrmReductionFixture");
        var originalClassifier=CavePopulationTargets.ClassifyPrefab;
        try
        {
            var spawn=typeof(PopulationSample).GetMethod("SpawnRandom",Flags)!;
            native.Patch(spawn,prefix:new HarmonyMethod(typeof(WyrmTests),nameof(FakeSpawn)));
            foreach(var m in typeof(PopulationAnalytics).GetMethods(BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).Where(m=>m.Name=="SendRecord"))
                native.Patch(m,prefix:new HarmonyMethod(typeof(WyrmTests),nameof(SkipAnalytics)));
            Check(Execute(Sample(100))==8,"Native fixture did not reproduce target eight");CavePopulationTargets.Attach(patch);
            foreach(int level in new[]{0,1,90,100,101,1000,int.MaxValue})
            {
                var sample=Sample(level);Check(Execute(sample)==6,"Wyrm target was not reduced at cave layer "+level);
                Check(sample.CurrentPopulation==8,"Reduced target changed saved hidden population");
            }
            Check(Execute(Sample(100,false))==8,"Surface population was reduced");
            Check(Execute(Sample(100,true,true))==8,"Mixed population changed unrelated creature spawns");
            var populated=Sample(100,true,false,8);Check(Execute(populated)==0&&populated.CurrentPopulation==8&&populated.SpawnedEntities.Count==8,"Existing mobs deleted or hidden count inflated");
            Core.Config.WyrmMultiplier=1;Check(Execute(Sample(100))==8,"Native multiplier did not restore target");Core.Config.WyrmMultiplier=.75f;
            Check(CavePopulationTargets.ReducedTarget(1,0,.75f,0)==0&&CavePopulationTargets.ReducedTarget(1,0,.75f,.5)==1&&CavePopulationTargets.ReducedTarget(4,0,.75f)==3&&CavePopulationTargets.ReducedTarget(100,0,.75f)==75,"Whole-creature rounding changed");
            int smallTotal=0;
            for(int i=0;i<40;i++)
            {
                var small=Sample(100);Field(small,"<CurrentPopulation>k__BackingField",1);Field(small,"<MaximumPopulation>k__BackingField",1);
                int first=Execute(small);smallTotal+=first;Check(Execute(small)==first,"Rounding target changed between population ticks");
            }
            Check(smallTotal==30,"Small populations rounded away the 25 percent reduction");
            Check(Execute(Sample(100,kind:Creature.Turabada))==8,"Existing config unexpectedly reduces Turabadas");
            Core.Config.TurabadaMultiplier=Settings.Parse("cave_turabada_multiplier=0.75").TurabadaMultiplier;
            long turabadaBefore=CavePopulationTargets.TurabadaReduced;
            long wyrmBefore=CavePopulationTargets.WyrmReduced;
            foreach(int level in new[]{0,1,20,40,60,90,100,175,int.MaxValue})
            {
                var sample=Sample(level,kind:Creature.Turabada);
                Check(Execute(sample)==6&&sample.CurrentPopulation==8,"Native Turabada target or saved population changed incorrectly at cave layer "+level);
            }
            Check(CavePopulationTargets.TurabadaReduced-turabadaBefore==9&&CavePopulationTargets.WyrmReduced==wyrmBefore,
                "Turabada instrumentation counted the wrong species");
            Check(Execute(Sample(100,cave:false,kind:Creature.Turabada))==8,"Surface Turabadas were reduced");
            Check(Execute(Sample(100,mixed:true,kind:Creature.Turabada))==8,"Mixed population changed unrelated spawns");
            Check(Execute(Sample(100,kind:Creature.Turabada,oneOff:true))==8,"Scripted one-off population was reduced");
            var turabadas=Sample(100,existing:8,kind:Creature.Turabada);
            Check(Execute(turabadas)==0&&turabadas.SpawnedEntities.Count==8&&turabadas.CurrentPopulation==8,
                "Turabada target removed existing creatures or changed saved population");
            Check(Execute(Sample(100,existing:3,kind:Creature.Turabada))==3,"Existing Turabadas were not counted toward the reduced refill target");
            Core.Config.WyrmMultiplier=1;
            Check(Execute(Sample(100))==8&&Execute(Sample(100,kind:Creature.Turabada))==6,"The two creature settings are coupled");
            Core.Config.WyrmMultiplier=.75f;
            int wyrmSingles=0,turabadaSingles=0;
            for(int i=0;i<40;i++)
                foreach(var kind in new[]{Creature.Wyrm,Creature.Turabada})
                {
                    var small=Sample(100,kind:kind);
                    Field(small,"<CurrentPopulation>k__BackingField",1);Field(small,"<MaximumPopulation>k__BackingField",1);
                    int first=Execute(small);Check(Execute(small)==first,"Species rounding changed between population ticks");
                    if(kind==Creature.Wyrm)wyrmSingles+=first;else turabadaSingles+=first;
                }
            Check(wyrmSingles==30&&turabadaSingles==30,"Interleaving species changed the whole-creature reduction");
            foreach(string invalid in new[]{"NaN","Infinity","-0.01","1.01"})
            {
                bool invalidRejected=false;
                try{Settings.Parse("cave_turabada_multiplier="+invalid);}catch(FormatException){invalidRejected=true;}
                Check(invalidRejected,"Invalid Turabada multiplier was accepted: "+invalid);
            }
            bool rejected=false;try{CavePopulationTargets.Rewrite(new[]{new CodeInstruction(OpCodes.Ret)}).ToArray();}catch(NotSupportedException){rejected=true;}
            Check(rejected,"Changed population target IL was accepted");
            patch.UnpatchSelf();Check(Execute(Sample(100))==8&&Execute(Sample(100,kind:Creature.Turabada))==8,"Rollback did not restore native counts");
            Console.WriteLine("PASS actual native population loop: both species 8->6 across generated mine layers, surface/mixed and scripted Turabada spawns native, existing populations retained, independent settings and rounding, validated config/IL and rollback. Spawn/component lookup are isolated fixtures.");
        }
        finally{CavePopulationTargets.ClassifyPrefab=originalClassifier;Core.Config.WyrmMultiplier=.75f;Core.Config.TurabadaMultiplier=1f;patch.UnpatchSelf();native.UnpatchSelf();}
    }
}
