using System;
using System.Globalization;
using System.IO;

namespace DailyGarbageCollection
{
    internal static class DailySchedule
    {
        internal static TimeZoneInfo ResolveEastern()
        {
            foreach (string id in new[] { "America/New_York", "Eastern Standard Time" })
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { }
                catch (InvalidTimeZoneException) { }
            }
            throw new InvalidOperationException("Eastern time zone unavailable; no changes applied");
        }
        internal static bool ShouldRun(DateTime local, string? lastDate)
        {
            string today = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return local.Hour == 6 && local.Minute < 5 && (lastDate == null || string.CompareOrdinal(lastDate, today) < 0);
        }
        internal static string? ReadMarker(string path)
        {
            if (!File.Exists(path)) return null;
            string value = File.ReadAllText(path).Trim();
            if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidDataException("Invalid daily collection marker");
            return value;
        }
        internal static void WriteMarker(string path, string value)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, value + "\n");
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }
    }
}
