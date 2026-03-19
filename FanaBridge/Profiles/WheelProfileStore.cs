using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;

namespace FanaBridge.Profiles
{
    /// <summary>
    /// Loads <see cref="WheelProfile"/> definitions and provides lookup by
    /// SDK wheel/module type.
    ///
    /// Thread-safe via snapshot-and-swap: all read methods capture a local
    /// reference to the current immutable snapshot, so concurrent reads never
    /// see partially-populated state even if <see cref="Reload"/> is running
    /// on another thread.
    ///
    /// Loading order (later entries override earlier ones with the same ID):
    ///   1. Built-in profiles embedded in the plugin DLL as assembly resources.
    ///      These are immutable and cannot be modified by users.
    ///   2. User profiles from a writable directory on disk (future).
    ///      Users can create/edit these to support new wheels.
    /// </summary>
    public static class WheelProfileStore
    {
        /// <summary>
        /// Immutable snapshot of loaded profiles.  Replaced atomically by
        /// <see cref="Reload"/> and <see cref="DeleteUserProfile"/>.
        /// </summary>
        private class ProfileSnapshot
        {
            public IReadOnlyList<WheelProfile> All { get; }
            public IReadOnlyDictionary<string, WheelProfile> ById { get; }

            public ProfileSnapshot(
                List<WheelProfile> all,
                Dictionary<string, WheelProfile> byId)
            {
                All = all;
                ById = byId;
            }
        }

        private static volatile ProfileSnapshot _snapshot;

        /// <summary>
        /// Returns the current snapshot, loading if necessary.
        /// All public read methods call this to get a consistent view.
        /// </summary>
        private static ProfileSnapshot GetSnapshot()
        {
            var snap = _snapshot;
            if (snap != null) return snap;
            EnsureLoaded();
            return _snapshot;
        }

        /// <summary>
        /// Loads all profiles from embedded resources and user directory.
        /// Safe to call multiple times — subsequent calls are no-ops.
        /// </summary>
        public static void EnsureLoaded()
        {
            if (_snapshot != null) return;
            Reload();
        }

        /// <summary>
        /// Reloads all profiles from scratch (embedded + user directory).
        /// Called after the wizard saves a new profile so the change takes
        /// effect immediately without restarting SimHub.
        /// Builds a new snapshot and swaps it in atomically.
        /// </summary>
        public static void Reload()
        {
            var all = new List<WheelProfile>();
            var byId = new Dictionary<string, WheelProfile>(StringComparer.OrdinalIgnoreCase);

            // 1. Built-in profiles: embedded in the assembly (immutable)
            LoadFromEmbeddedResources(all, byId);

            // 2. User profiles: writable directory on disk (overrides built-in)
            string userDir = GetUserProfileDirectory();
            if (userDir != null)
                LoadFromDirectory(userDir, all, byId);

            Interlocked.Exchange(ref _snapshot, new ProfileSnapshot(all, byId));

            SimHub.Logging.Current.Info(
                "WheelProfileStore: Loaded " + all.Count + " profile(s) (" + byId.Count + " unique IDs)");
        }

        /// <summary>
        /// Returns the user-writable directory for custom wheel profiles.
        /// Creates it if it doesn't exist.  Located alongside the plugin DLL.
        /// </summary>
        public static string GetUserProfileDirectory()
        {
            try
            {
                string dllDir = Path.GetDirectoryName(
                    typeof(WheelProfileStore).Assembly.Location) ?? ".";
                string userDir = Path.Combine(dllDir, "FanaBridge", "Profiles");
                if (!Directory.Exists(userDir))
                    Directory.CreateDirectory(userDir);
                return userDir;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "WheelProfileStore: Could not create user profile directory: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Loads profile JSON from assembly embedded resources.
        /// Resource names matching "*.Profiles.*.json" are treated as profiles.
        /// </summary>
        private static void LoadFromEmbeddedResources(
            List<WheelProfile> all, Dictionary<string, WheelProfile> byId)
        {
            var assembly = typeof(WheelProfileStore).Assembly;

            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                // Embedded resource names follow: {RootNamespace}.{RelativePath}.{filename}
                // e.g. "FanaBridge.Resources.Profiles.PSWBMW.json"
                if (!resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (resourceName.IndexOf(".Profiles.", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                // Exclude the JSON schema file — it has no 'id' and is not a profile.
                if (resourceName.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    using (var stream = assembly.GetManifestResourceStream(resourceName))
                    {
                        if (stream == null) continue;
                        using (var reader = new StreamReader(stream))
                        {
                            string json = reader.ReadToEnd();
                            var profile = JsonConvert.DeserializeObject<WheelProfile>(json);

                            if (profile?.Id == null)
                            {
                                SimHub.Logging.Current.Warn(
                                    "WheelProfileStore: Skipping embedded resource with no 'id': " + resourceName);
                                continue;
                            }

                            byId[profile.Id] = profile;
                            all.Add(profile);
                            profile.Source = ProfileSource.BuiltIn;
                            profile.SourcePath = resourceName;
                            SimHub.Logging.Current.Info(
                                "WheelProfileStore: Loaded built-in profile '" + profile.Id +
                                "' (" + profile.Name + ")");
                        }
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        "WheelProfileStore: Failed to load embedded resource " + resourceName + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Loads all .json files from a directory into the store.
        /// Files loaded later override earlier ones with the same ID.
        /// </summary>
        private static void LoadFromDirectory(
            string directory, List<WheelProfile> all, Dictionary<string, WheelProfile> byId)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string file in Directory.GetFiles(directory, "*.json"))
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var profile = JsonConvert.DeserializeObject<WheelProfile>(json);

                    if (profile?.Id == null)
                    {
                        SimHub.Logging.Current.Warn(
                            "WheelProfileStore: Skipping file with no 'id': " + file);
                        continue;
                    }

                    profile.Source = ProfileSource.User;
                    profile.SourcePath = file;

                    bool wasBuiltIn = byId.TryGetValue(profile.Id, out var existing)
                        && existing.Source == ProfileSource.BuiltIn;
                    bool wasUser = byId.TryGetValue(profile.Id, out var existingUser)
                        && existingUser.Source == ProfileSource.User;

                    // User profile wins in the auto-resolution dictionary,
                    // but the built-in remains in all for the profile picker.
                    byId[profile.Id] = profile;
                    all.Add(profile);

                    if (wasUser)
                    {
                        SimHub.Logging.Current.Warn(
                            "WheelProfileStore: Duplicate user profile '" + profile.Id +
                            "' — " + Path.GetFileName(file) + " overrides earlier file");
                    }
                    else if (wasBuiltIn)
                    {
                        SimHub.Logging.Current.Info(
                            "WheelProfileStore: User profile '" + profile.Id +
                            "' overrides built-in profile");
                    }

                    SimHub.Logging.Current.Info(
                        "WheelProfileStore: Loaded user profile '" + profile.Id +
                        "' (" + profile.Name + ") from " + Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Warn(
                        "WheelProfileStore: Failed to load " + file + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Looks up a profile by its ID.  When multiple profiles share the
        /// same ID (built-in + user), returns the auto-resolution winner.
        /// Use <see cref="FindAllForWheel"/> to get all candidates.
        /// </summary>
        public static WheelProfile GetById(string id)
        {
            var snap = GetSnapshot();
            return snap.ById.TryGetValue(id, out var profile) ? profile : null;
        }

        /// <summary>
        /// Looks up a specific profile by ID and source.  This allows
        /// retrieving a built-in profile even when a user profile with the
        /// same ID exists.
        /// </summary>
        public static WheelProfile GetById(string id, ProfileSource source)
        {
            var snap = GetSnapshot();
            return snap.All.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)
                && p.Source == source);
        }

        /// <summary>
        /// Finds the best matching profile for the given SDK wheel/module type.
        /// For hub+module combos, tries "{wheelType}_{moduleType}" first,
        /// then falls back to "{wheelType}" alone.
        /// Returns null if no profile matches.
        /// </summary>
        public static WheelProfile FindByWheelType(string wheelType, string moduleType = null)
        {
            return FindByWheelType(wheelType, moduleType, overrideId: null);
        }

        /// <summary>
        /// Finds a profile for the given SDK wheel/module type, optionally
        /// respecting an explicit user override.
        /// </summary>
        /// <param name="overrideId">
        /// When non-null, look up this specific profile ID first.  If the
        /// profile no longer exists (deleted file, etc.), fall through to
        /// normal auto-resolution.
        /// </param>
        public static WheelProfile FindByWheelType(
            string wheelType, string moduleType, string overrideId)
        {
            var snap = GetSnapshot();

            // 0. Explicit user override (from plugin settings).
            //    An override key is "ID:source" (e.g. "PHUB_PBMR:BuiltIn")
            //    to disambiguate built-in vs. user profiles with the same ID.
            if (!string.IsNullOrEmpty(overrideId))
            {
                var overridden = ResolveOverrideKey(overrideId);
                if (overridden != null)
                    return overridden;
                // Override didn't resolve — fall through to auto
            }

            // 1. Try compound key first (hub + module)
            if (!string.IsNullOrEmpty(moduleType))
            {
                string compoundId = wheelType + "_" + moduleType;
                if (snap.ById.TryGetValue(compoundId, out var compound))
                    return compound;
            }

            // Fall back to wheel-only key
            if (snap.ById.TryGetValue(wheelType, out var profile))
                return profile;

            // Scan by match criteria (for profiles that use non-standard IDs)
            foreach (var p in snap.ById.Values)
            {
                if (p.Match == null) continue;

                bool wheelMatch = string.Equals(
                    p.Match.WheelType, wheelType, StringComparison.OrdinalIgnoreCase);

                if (!wheelMatch) continue;

                if (string.IsNullOrEmpty(moduleType))
                {
                    if (string.IsNullOrEmpty(p.Match.ModuleType))
                        return p;
                }
                else
                {
                    if (string.Equals(p.Match.ModuleType, moduleType, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns all loaded profiles.
        /// </summary>
        public static IEnumerable<WheelProfile> GetAll()
        {
            return GetSnapshot().All;
        }

        /// <summary>
        /// Returns every profile that would match the given wheel/module type,
        /// regardless of priority.  Used by the settings UI to populate the
        /// profile picker ComboBox.  Results may include both built-in and
        /// user profiles — even when they share the same ID.
        /// </summary>
        public static List<WheelProfile> FindAllForWheel(
            string wheelType, string moduleType = null)
        {
            var snap = GetSnapshot();
            var results = new List<WheelProfile>();

            foreach (var p in snap.All)
            {
                if (p.Match == null) continue;

                bool wheelMatch = string.Equals(
                    p.Match.WheelType, wheelType, StringComparison.OrdinalIgnoreCase);
                if (!wheelMatch) continue;

                // For module-capable wheels, include profiles that either
                // match the specific module or have no module requirement.
                if (!string.IsNullOrEmpty(moduleType))
                {
                    bool moduleMatch = string.IsNullOrEmpty(p.Match.ModuleType)
                        || string.Equals(p.Match.ModuleType, moduleType, StringComparison.OrdinalIgnoreCase);
                    if (moduleMatch)
                        results.Add(p);
                }
                else
                {
                    if (string.IsNullOrEmpty(p.Match.ModuleType))
                        results.Add(p);
                }
            }

            return results;
        }

        /// <summary>
        /// Deletes a user-created profile from disk and removes it from the
        /// in-memory store.  Returns true if the file was deleted.
        /// Built-in profiles cannot be deleted.  If a built-in profile with
        /// the same ID exists, it is restored as the auto-resolution winner.
        /// </summary>
        public static bool DeleteUserProfile(string profileId)
        {
            var snap = GetSnapshot();

            var userProfile = snap.All.FirstOrDefault(p =>
                string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)
                && p.Source == ProfileSource.User);

            if (userProfile == null)
            {
                SimHub.Logging.Current.Warn(
                    "WheelProfileStore: No user profile found with ID '" + profileId + "'");
                return false;
            }

            try
            {
                if (!string.IsNullOrEmpty(userProfile.SourcePath) && File.Exists(userProfile.SourcePath))
                    File.Delete(userProfile.SourcePath);

                // Build a new snapshot without the deleted profile.
                // Iterate in load order so "last wins" for byId is preserved.
                var newAll = new List<WheelProfile>(snap.All.Count);
                var newById = new Dictionary<string, WheelProfile>(StringComparer.OrdinalIgnoreCase);

                foreach (var p in snap.All)
                {
                    if (p == userProfile) continue;
                    newAll.Add(p);
                    newById[p.Id] = p; // last wins (user > built-in)
                }

                Interlocked.Exchange(ref _snapshot, new ProfileSnapshot(newAll, newById));

                SimHub.Logging.Current.Info(
                    "WheelProfileStore: Deleted user profile '" + profileId + "'");
                return true;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Warn(
                    "WheelProfileStore: Failed to delete profile '" + profileId + "': " + ex.Message);
                return false;
            }
        }

        // ── Override key helpers ─────────────────────────────────────────

        /// <summary>
        /// Builds a stable key for storing profile overrides in settings.
        /// Format: "ProfileId:Source" (e.g. "PHUB_PBMR:BuiltIn").
        /// This disambiguates built-in vs. user profiles that share an ID.
        /// </summary>
        public static string MakeOverrideKey(WheelProfile profile)
        {
            return profile.Id + ":" + profile.Source;
        }

        /// <summary>
        /// Resolves an override key (from <see cref="MakeOverrideKey"/>) back
        /// to a profile instance.  Returns null if not found.
        /// </summary>
        public static WheelProfile ResolveOverrideKey(string overrideKey)
        {
            if (string.IsNullOrEmpty(overrideKey))
                return null;

            var snap = GetSnapshot();

            int sep = overrideKey.LastIndexOf(':');
            if (sep > 0 && sep < overrideKey.Length - 1)
            {
                string id = overrideKey.Substring(0, sep);
                string sourceStr = overrideKey.Substring(sep + 1);

                if (Enum.TryParse(sourceStr, true, out ProfileSource source))
                    return GetById(id, source);
            }

            // Legacy / simple key — fall back to ById lookup
            return snap.ById.TryGetValue(overrideKey, out var p) ? p : null;
        }

        /// <summary>
        /// Strips SDK enum prefixes to get the short code for matching.
        /// e.g. "FS_WHEEL_SWTYPE_PSWBMW" → "PSWBMW"
        /// </summary>
        public static string StripWheelPrefix(string enumName)
        {
            const string prefix = "FS_WHEEL_SWTYPE_";
            if (enumName != null && enumName.StartsWith(prefix))
                return enumName.Substring(prefix.Length);
            return enumName;
        }

        /// <summary>
        /// Strips SDK module enum prefixes to get the short code.
        /// e.g. "FS_WHEEL_SW_MODULETYPE_PBMR" → "PBMR"
        /// </summary>
        public static string StripModulePrefix(string enumName)
        {
            const string prefix = "FS_WHEEL_SW_MODULETYPE_";
            if (enumName != null && enumName.StartsWith(prefix))
                return enumName.Substring(prefix.Length);
            return enumName;
        }
    }
}
