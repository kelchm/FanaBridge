using System;
using System.Collections.Generic;
using System.IO;
using FanaBridge.Core.Devices.Profiles;

namespace FanaBridge.Tests.TestDoubles
{
    /// <summary>
    /// Writes user profiles into the directory the real
    /// <see cref="WheelProfileStore"/> reads (next to the test binary), and on
    /// dispose removes them, restores the file-enumeration seam and reloads.
    /// </summary>
    internal sealed class TempUserProfiles : IDisposable
    {
        private readonly List<string> _files = new List<string>();

        public string Directory { get; } = WheelProfileStore.GetUserProfileDirectory();

        /// <summary>
        /// Writes a profile with <paramref name="revLeds"/> rev LEDs and no
        /// display, so it is never a bare profile. Does not reload.
        /// </summary>
        public string Write(
            string fileName, string id, string wheelType, string? moduleType = null,
            int revLeds = 3, string? name = null)
        {
            var leds = new List<string>();
            for (int i = 0; i < revLeds; i++)
                leds.Add("{ \"channel\": \"revRgb\", \"hwIndex\": " + i +
                    ", \"role\": \"rev\", \"label\": \"Rev " + (i + 1) + "\" }");

            string match = "\"wheelType\": \"" + wheelType + "\"" +
                (moduleType == null ? "" : ", \"moduleType\": \"" + moduleType + "\"");

            return WriteJson(fileName, "{ \"schemaVersion\": 2, \"id\": \"" + id + "\", " +
                "\"name\": \"" + (name ?? id + " (" + fileName + ")") + "\", " +
                "\"match\": { " + match + " }, \"display\": \"none\", " +
                "\"leds\": [ " + string.Join(", ", leds) + " ] }");
        }

        public string WriteJson(string fileName, string json)
        {
            string path = Path.Combine(Directory, fileName);
            File.WriteAllText(path, json);
            if (!_files.Contains(path))
                _files.Add(path);
            return path;
        }

        public void Reload() => WheelProfileStore.Reload();

        public void Dispose()
        {
            foreach (var file in _files)
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
            WheelProfileStore.EnumerateUserFiles = WheelProfileStore.DefaultEnumerateUserFiles;
            WheelProfileStore.Reload();
        }
    }
}
