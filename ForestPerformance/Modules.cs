using System;
using System.Collections.Generic;
using System.Linq;
using MelonLoader;

namespace ForestPerformance.Support
{
    // A plain helper, deliberately not a MelonMod. The host forwards lifecycle
    // callbacks once and gives every component the same family logger/root.
    public abstract class Module
    {
        protected internal MelonLogger.Instance LoggerInstance { get; internal set; } = null!;
        protected internal string DataDirectory { get; internal set; } = "";
        public virtual void OnInitializeMelon() { }
        public virtual void OnUpdate() { }
        public virtual void OnApplicationQuit() { }
    }
    internal sealed class ModuleGroup
    {
        private readonly List<Module> active = new List<Module>();
        internal void Start(Module module, MelonLogger.Instance logger, string directory)
        {
            module.LoggerInstance = logger; module.DataDirectory = directory;
            try { module.OnInitializeMelon(); active.Add(module); }
            catch (Exception error)
            {
                logger.Error("Component disabled: " + module.GetType().FullName + ": " + error);
                try { module.OnApplicationQuit(); } catch (Exception cleanup) { logger.Error("Component cleanup: " + cleanup); }
            }
        }
        internal void Tick()
        {
            for (int i = 0; i < active.Count; i++)
            {
                Module module = active[i];
                try { module.OnUpdate(); }
                catch (Exception error)
                {
                    module.LoggerInstance.Error("Component stopped: " + module.GetType().FullName + ": " + error);
                    active.RemoveAt(i--);
                    try { module.OnApplicationQuit(); } catch (Exception cleanup) { module.LoggerInstance.Error("Component cleanup: " + cleanup); }
                }
            }
        }
        internal void Stop()
        {
            for (int i = active.Count - 1; i >= 0; i--)
            {
                Module module = active[i];
                try { module.OnApplicationQuit(); }
                catch (Exception error) { module.LoggerInstance.Error("Component shutdown: " + error); }
            }
            active.Clear();
        }
        internal static void CheckLegacy(params string[] names)
        {
            string[] loaded = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name).Where(n => names.Contains(n)).ToArray();
            if (loaded.Length > 0) throw new InvalidOperationException("Remove replaced standalone DLLs before startup: " + string.Join(", ", loaded));
        }
    }
}
