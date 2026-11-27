using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Alta;
using Alta.Populations;
using Alta.Saving;
using Alta.Serialization;

namespace ForestPerformance
{
    internal static class PopulationRestorePrewarm
    {
        // Uses the same version-specific activator and serializer caches as a
        // real restore. The scratch data never leaves this method.
        internal static void Prepare(PopulationSetup? definition = null)
        {
            var setup = SavingSystem.GetSetupOfData(new BasicPopulationFolderSaveData());
            var version = setup.LatestSetup;
            if (setup.Topic != typeof(BasicPopulationFolder) || setup.Hash != 1931537627u ||
                setup.LatestVersion != 1 || version.Type != typeof(BasicPopulationFolderSaveData))
                throw new NotSupportedException("Unexpected forest population save format");

            var original = (BasicPopulationFolderSaveData)version.CreateInstance();
            original.definition = definition;
            original.time = TimeSpan.FromSeconds(10);
            original.accumulatedTime = 1.5f;
            original.population = 3;
            var result = (BasicPopulationFolderSaveData)version.CreateInstance();
            ChunkVersioning chunk = SavingSystem.LatestChunkVersioning;
            uint[] data = new uint[16];
            using (var writer = new StreamWriter(data))
            {
                version.Serialize(original, writer, chunk);
                uint bitsWritten = writer.BitIndex;
                using var reader = new StreamReader(data, (int)((bitsWritten + 7) / 8));
                version.Serialize(result, reader, chunk);
                if (reader.BitIndex != bitsWritten || !ReferenceEquals(result.definition, definition) || result.time != original.time ||
                    result.accumulatedTime != original.accumulatedTime || result.population != original.population)
                    throw new InvalidOperationException("Forest population serializer scratch round trip disagreed");
            }
        }

        internal static PopulationSetup WarmDefinitions()
        {
            const BindingFlags fields = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var baseType = typeof(HashedGeneralValue<PopulationSetup>);
            var items = baseType.GetField("items", fields) ?? throw new MissingFieldException(baseType.FullName, "items");
            var loading = baseType.GetField("isInResourceLoading", fields) ?? throw new MissingFieldException(baseType.FullName, "isInResourceLoading");
            var registry = typeof(HashedGeneralValue).GetField("allValues", fields)?.GetValue(null)
                as Dictionary<Type, IEnumerable<HashedGeneralValue>> ?? throw new MissingFieldException(nameof(HashedGeneralValue), "allValues");
            var setupType = typeof(PopulationSetup);
            object? previousItems = items.GetValue(null);
            if ((bool)loading.GetValue(null)!) throw new InvalidOperationException("Population definitions are already loading");
            bool registered = registry.TryGetValue(setupType, out var previousValues);
            try
            {
                // Native Resources.LoadAll runs on Unity's main thread here.
                var definition = PopulationSetup.All.FirstOrDefault(item => item.Hash != 0);
                return definition ?? throw new InvalidOperationException("Population definitions are unavailable at startup");
            }
            catch
            {
                // A premature load may publish an empty native collection.
                // Restore the prior lookup state so the first real load can retry.
                items.SetValue(null, previousItems);
                loading.SetValue(null, false);
                if (registered) registry[setupType] = previousValues!;
                else registry.Remove(setupType);
                throw;
            }
        }
    }
}
