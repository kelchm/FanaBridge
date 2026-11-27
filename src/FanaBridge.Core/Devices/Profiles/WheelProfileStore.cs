using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FanaBridge.Core.Devices.Identity;
using Newtonsoft.Json;

namespace FanaBridge.Core.Devices.Profiles
{
    /// <summary>
    /// Loads <see cref="WheelProfile"/> definitions and provides lookup by
    /// decoded wheel/module type.
    ///
    /// Thread-safe via snapshot-and-swap: all read methods capture a local
    /// reference to the current immutable snapshot, so concurrent reads never
    /// see partially-populated state even if <see cref="Reload"/> is running
    /// on another thread.
    ///
    /// Loading order is deterministic, so every lookup resolves the same way
    /// however the directory happens to be enumerated:
    ///   1. Built-in profiles embedded in the plugin DLL as assembly resources,
    ///      sorted by resource name (ordinal). These are immutable.
    ///   2. User profiles from a writable directory next to the plugin DLL,
    ///      sorted by file name (ordinal, ignoring case). Users can create/edit
    ///      these to support new wheels.
    ///
    /// Ids are unique within a source: the first profile in that order keeps
    /// the id, and a later duplicate is rejected with a warning naming both
    /// files. Across sources, a user profile takes a built-in's id in the id
    /// index, while the built-in stays in <see cref="GetAll"/> so the profile
    /// picker can still offer it.
    ///
    /// <see cref="Resolve"/> is the single rule mapping an attachment to a
    /// profile; registration, runtime capability resolution and the settings
    /// picker all go through it.
    /// </summary>
    public static class WheelProfileStore
    {
        /// <summary>
        /// Immutable snapshot of loaded profiles.  Replaced atomically by
        /// <see cref="Reload"/>.
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

            // 2. User profiles: writable directory on disk. A user profile takes
            //    a built-in's id in byId; the built-in stays in all.
            string userDir = GetUserProfileDirectory();
            if (userDir != null)
                LoadFromDirectory(userDir, all, byId);

            Interlocked.Exchange(ref _snapshot, new ProfileSnapshot(all, byId));

            Log.Info(
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
                Log.Warn(
                    "WheelProfileStore: Could not create user profile directory: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Loads profile JSON from assembly embedded resources, in resource-name
        /// order. Resource names matching "*.Profiles.*.json" are treated as
        /// profiles.
        /// </summary>
        private static void LoadFromEmbeddedResources(
            List<WheelProfile> all, Dictionary<string, WheelProfile> byId)
        {
            var assembly = typeof(WheelProfileStore).Assembly;
            var loaded = new Dictionary<string, WheelProfile>(StringComparer.OrdinalIgnoreCase);

            foreach (string resourceName in assembly.GetManifestResourceNames()
                         .OrderBy(n => n, StringComparer.Ordinal))
            {
                // Embedded resource names follow: {RootNamespace}.{RelativePath}.{filename}
                // e.g. "FanaBridge.Core.Resources.Profiles.PSWBMW.json"
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
                                Log.Warn(
                                    "WheelProfileStore: Skipping embedded resource with no 'id': " + resourceName);
                                continue;
                            }

                            CanonicalizeMatch(profile);

                            if (loaded.TryGetValue(profile.Id, out var first))
                            {
                                Log.Warn(
                                    "WheelProfileStore: Built-in profile '" + profile.Id + "' in " +
                                    resourceName + " ignored: id already used by " + first.SourcePath);
                                continue;
                            }

                            profile.Source = ProfileSource.BuiltIn;
                            profile.SourcePath = resourceName;
                            loaded[profile.Id] = profile;
                            byId[profile.Id] = profile;
                            all.Add(profile);
                            Log.Info(
                                "WheelProfileStore: Loaded built-in profile '" + profile.Id +
                                "' (" + profile.Name + ")");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(
                        "WheelProfileStore: Failed to load embedded resource " + resourceName + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Test seam: lists the user profile files in a directory. The loader
        /// sorts whatever this returns, so the order it comes back in never
        /// matters.
        /// </summary>
        internal static Func<string, IEnumerable<string>> EnumerateUserFiles = DefaultEnumerateUserFiles;

        /// <summary>The production <see cref="EnumerateUserFiles"/>.</summary>
        internal static IEnumerable<string> DefaultEnumerateUserFiles(string directory) =>
            Directory.GetFiles(directory, "*.json");

        /// <summary>
        /// Loads all .json files from a directory into the store, in file-name
        /// order. A file reusing the id of an earlier user file is rejected.
        /// </summary>
        private static void LoadFromDirectory(
            string directory, List<WheelProfile> all, Dictionary<string, WheelProfile> byId)
        {
            if (!Directory.Exists(directory))
                return;

            var loaded = new Dictionary<string, WheelProfile>(StringComparer.OrdinalIgnoreCase);
            var files = EnumerateUserFiles(directory)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => Path.GetFileName(f), StringComparer.Ordinal);

            foreach (string file in files)
            {
                try
                {
                    string json = File.ReadAllText(file);
                    var profile = JsonConvert.DeserializeObject<WheelProfile>(json);

                    if (profile?.Id == null)
                    {
                        Log.Warn(
                            "WheelProfileStore: Skipping file with no 'id': " + file);
                        continue;
                    }

                    CanonicalizeMatch(profile);

                    if (loaded.TryGetValue(profile.Id, out var first))
                    {
                        Log.Warn(
                            "WheelProfileStore: User profile '" + profile.Id + "' in " +
                            Path.GetFileName(file) + " ignored: id already used by " +
                            Path.GetFileName(first.SourcePath) + ". Give it a unique id " +
                            "and reselect it in FanaBridge's settings to use it.");
                        continue;
                    }

                    profile.Source = ProfileSource.User;
                    profile.SourcePath = file;

                    if (profile.SchemaVersion < WheelProfile.CurrentSchemaVersion)
                    {
                        Log.Warn(
                            "WheelProfileStore: User profile '" + profile.Id +
                            "' uses schema version " + profile.SchemaVersion +
                            " (current is " + WheelProfile.CurrentSchemaVersion +
                            "). Old channel names were migrated automatically, " +
                            "but please update the profile to use the current schema.");
                    }
                    else if (profile.SchemaVersion > WheelProfile.CurrentSchemaVersion)
                    {
                        Log.Warn(
                            "WheelProfileStore: User profile '" + profile.Id +
                            "' uses newer schema version " + profile.SchemaVersion +
                            " (current is " + WheelProfile.CurrentSchemaVersion +
                            "). This build may not load it correctly.");
                    }

                    // Only built-ins are in byId before this loop's own entries,
                    // and those are caught by the duplicate check above.
                    bool wasBuiltIn = byId.ContainsKey(profile.Id);

                    // User profile wins in the id index, but the built-in
                    // remains in all for the profile picker.
                    loaded[profile.Id] = profile;
                    byId[profile.Id] = profile;
                    all.Add(profile);

                    if (wasBuiltIn)
                    {
                        Log.Info(
                            "WheelProfileStore: User profile '" + profile.Id +
                            "' overrides built-in profile");
                    }

                    Log.Info(
                        "WheelProfileStore: Loaded user profile '" + profile.Id +
                        "' (" + profile.Name + ") from " + Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    Log.Warn(
                        "WheelProfileStore: Failed to load " + file + ": " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Looks up a profile by its ID.  When a built-in and a user profile
        /// share the ID, returns the user profile.
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
        /// same ID exists.  Ids are unique within a source, so this is
        /// unambiguous.
        /// </summary>
        public static WheelProfile GetById(string id, ProfileSource source)
        {
            var snap = GetSnapshot();
            return snap.All.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)
                && p.Source == source);
        }

        /// <summary>
        /// Finds the profile for the given decoded wheel/module type, with no
        /// override. See <see cref="Resolve"/> for the rule.
        /// Returns null if no profile matches.
        /// </summary>
        public static WheelProfile FindByWheelType(string wheelType, string moduleType = null)
        {
            return Resolve(wheelType, moduleType, overrideKey: null, out _);
        }

        /// <summary>
        /// Finds a profile for the given decoded wheel/module type, optionally
        /// respecting an explicit user override. See <see cref="Resolve"/>.
        /// </summary>
        /// <param name="overrideId">
        /// An override key (<see cref="MakeOverrideKey"/>). It is honoured only
        /// when it resolves to a profile matching this attachment; otherwise
        /// normal resolution applies.
        /// </param>
        public static WheelProfile FindByWheelType(
            string wheelType, string moduleType, string overrideId)
        {
            return Resolve(wheelType, moduleType, overrideId, out _);
        }

        /// <summary>
        /// The single profile-resolution rule for an attachment, shared by
        /// device registration, runtime capability resolution and the settings
        /// picker so they can never disagree.
        ///
        /// The candidates are the profiles whose match equals the attachment
        /// exactly (<see cref="FindAllForWheel"/>). Of those, the result is:
        ///   1. the override, if it resolves and is a candidate;
        ///   2. else the candidate whose id is the match key
        ///      (<see cref="MakeMatchKey"/>), a user profile beating a built-in;
        ///   3. else the first candidate in load order.
        /// There is no bare-wheel fallback for a hub with a module attached:
        /// device matching is exact too, so such a profile's device could never
        /// report Connected.
        /// </summary>
        /// <param name="overrideProfile">
        /// The profile <paramref name="overrideKey"/> resolved to, or null when
        /// there was no override or it did not resolve. Non-null but different
        /// from the result means the override was ignored because it matches a
        /// different attachment; logging that is left to the caller.
        /// </param>
        /// <returns>The resolved profile, or null when nothing matches.</returns>
        public static WheelProfile Resolve(
            string wheelType, string moduleType, string overrideKey, out WheelProfile overrideProfile)
        {
            var snap = GetSnapshot();
            overrideProfile = ResolveOverrideKey(snap, overrideKey);

            var candidates = Candidates(snap, wheelType, moduleType);
            if (candidates.Count == 0)
                return null;

            // 1. Explicit user override (from plugin settings).
            //    An override key is "ID:source" (e.g. "PHUB_PBMR:BuiltIn")
            //    to disambiguate built-in vs. user profiles with the same ID.
            if (overrideProfile != null && candidates.Contains(overrideProfile))
                return overrideProfile;

            // Without an override, a bare profile (no LEDs, no display) never
            // wins over a candidate that can drive something — e.g. a user LED
            // profile for a hub beats the bare built-in hub profile named after
            // it. Only when every candidate is bare does a bare one resolve.
            var pool = candidates.Where(p => !p.IsBare).ToList();
            if (pool.Count == 0)
                pool = candidates;

            // 2. The candidate named after the attachment. Candidates come in
            //    load order (built-ins first), so the last one is the user's.
            string matchKey = MakeMatchKey(NormalizeWheelType(wheelType), moduleType);
            var named = pool.LastOrDefault(p =>
                string.Equals(p.Id, matchKey, StringComparison.OrdinalIgnoreCase));
            if (named != null)
                return named;

            // 3. First candidate in load order (profiles with non-standard ids).
            return pool[0];
        }

        /// <summary>
        /// Returns all loaded profiles.
        /// </summary>
        public static IEnumerable<WheelProfile> GetAll()
        {
            return GetSnapshot().All;
        }

        /// <summary>
        /// Returns every profile whose match equals the given wheel/module type
        /// exactly — the candidates <see cref="Resolve"/> chooses from — in load
        /// order.  Used by the settings UI to populate the profile picker
        /// ComboBox.  Results may include both a built-in and a user profile
        /// sharing an ID.  A module-less profile is not a candidate for a hub
        /// with a module attached.
        /// </summary>
        public static List<WheelProfile> FindAllForWheel(
            string wheelType, string moduleType = null)
        {
            return Candidates(GetSnapshot(), wheelType, moduleType);
        }

        // Every lookup that combines several reads takes the snapshot once and
        // passes it down, so a concurrent Reload can't mix two snapshots' (equal
        // but distinct) profile instances within one resolution.
        private static List<WheelProfile> Candidates(
            ProfileSnapshot snap, string wheelType, string moduleType)
        {
            var results = new List<WheelProfile>();

            // A wheel/hub that isn't in the decode tables (e.g. EXT_INFO or
            // future hardware) resolves to a null code, which nothing matches.
            if (string.IsNullOrEmpty(wheelType))
                return results;

            // Normalize wheel type names that differ between the SimHub
            // managed DLL enum and our profile IDs. The managed DLL
            // (SimHub.FanatecManaged.dll) uses different names for some
            // wheels than the Fanatec SDK sources we based our profiles on.
            // This is a temporary workaround — see docs/reference/devices.md
            // for the full analysis of naming divergence across SDK versions.
            wheelType = NormalizeWheelType(wheelType);

            foreach (var p in snap.All)
            {
                if (p.Match == null) continue;

                bool wheelMatch = string.Equals(
                    p.Match.WheelType, wheelType, StringComparison.OrdinalIgnoreCase);
                if (!wheelMatch) continue;

                bool moduleMatch = string.IsNullOrEmpty(moduleType)
                    ? string.IsNullOrEmpty(p.Match.ModuleType)
                    : string.Equals(p.Match.ModuleType, moduleType, StringComparison.OrdinalIgnoreCase);
                if (moduleMatch)
                    results.Add(p);
            }

            return results;
        }

        /// <summary>
        /// Deletes a user-created profile from disk and reloads the store, so
        /// what is in memory afterwards is exactly what a fresh load of the
        /// remaining files gives.  Returns true if the profile was deleted.
        /// Built-in profiles cannot be deleted.  If a built-in profile with
        /// the same ID exists, it takes the ID back.
        /// </summary>
        public static bool DeleteUserProfile(string profileId)
        {
            var userProfile = GetById(profileId, ProfileSource.User);

            if (userProfile == null)
            {
                Log.Warn(
                    "WheelProfileStore: No user profile found with ID '" + profileId + "'");
                return false;
            }

            try
            {
                if (!string.IsNullOrEmpty(userProfile.SourcePath) && File.Exists(userProfile.SourcePath))
                    File.Delete(userProfile.SourcePath);
            }
            catch (Exception ex)
            {
                Log.Warn(
                    "WheelProfileStore: Failed to delete profile '" + profileId + "': " + ex.Message);
                return false;
            }

            Log.Info(
                "WheelProfileStore: Deleted user profile '" + profileId + "' (" +
                Path.GetFileName(userProfile.SourcePath) + ")");

            // The file is gone, so the delete succeeded; a failed rescan must not
            // leave the deleted profile resolvable. Fall back to dropping it from
            // the current snapshot (a built-in with the same id takes it back).
            try
            {
                Reload();
            }
            catch (Exception ex)
            {
                Log.Warn(
                    "WheelProfileStore: Reload after deleting '" + profileId + "' failed (" +
                    ex.Message + "); dropping it from the loaded profiles instead");
                RemoveFromSnapshot(userProfile);
            }
            return true;
        }

        private static void RemoveFromSnapshot(WheelProfile removed)
        {
            var snap = GetSnapshot();
            var all = snap.All.Where(p => !ReferenceEquals(p, removed)).ToList();
            // Same precedence as a load: built-ins first, then a user profile
            // takes the id.
            var byId = new Dictionary<string, WheelProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in all.Where(p => p.Source == ProfileSource.BuiltIn))
                byId[p.Id] = p;
            foreach (var p in all.Where(p => p.Source == ProfileSource.User))
                byId[p.Id] = p;
            Interlocked.Exchange(ref _snapshot, new ProfileSnapshot(all, byId));
        }

        // ── Key helpers ──────────────────────────────────────────────────

        /// <summary>
        /// Builds the profile-match key for a wheel/hub plus optional module,
        /// formed the same way compound profile IDs are: "{wheel}" alone, or
        /// "{wheel}_{module}". This is the single source of truth for the key
        /// format — callers (identity resolution, settings UI, wizard) must
        /// use it rather than concatenating by hand. Returns null when there is
        /// no wheel/hub code, so a stray module code can't yield a malformed
        /// "_{module}" key.
        /// </summary>
        public static string MakeMatchKey(string wheelCode, string moduleCode)
        {
            if (string.IsNullOrEmpty(wheelCode))
                return null;
            return string.IsNullOrEmpty(moduleCode)
                ? wheelCode
                : wheelCode + "_" + moduleCode;
        }

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
            return ResolveOverrideKey(GetSnapshot(), overrideKey);
        }

        private static WheelProfile ResolveOverrideKey(ProfileSnapshot snap, string overrideKey)
        {
            if (string.IsNullOrEmpty(overrideKey))
                return null;

            int sep = overrideKey.LastIndexOf(':');
            if (sep > 0 && sep < overrideKey.Length - 1)
            {
                string id = overrideKey.Substring(0, sep);
                string sourceStr = overrideKey.Substring(sep + 1);

                if (Enum.TryParse(sourceStr, true, out ProfileSource source))
                    return snap.All.FirstOrDefault(p =>
                        string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)
                        && p.Source == source);
            }

            // Legacy / simple key — fall back to ById lookup
            return snap.ById.TryGetValue(overrideKey, out var p) ? p : null;
        }

        /// <summary>
        /// Maps wheel type names that differ between the SimHub managed DLL
        /// and our profile IDs. The SimHub DLL (SimHub.FanatecManaged.dll)
        /// uses "BENTLEY" for the Podium Bentley GT3 (ID 21), while our
        /// profile and the GCS enum / Fanatec UI assets use "PSWBENT".
        ///
        /// This workaround will be removed when a more robust matching strategy
        /// replaces string-based matching (see docs/reference/devices.md).
        /// </summary>
        internal static string NormalizeWheelType(string wheelType)
        {
            return string.Equals(wheelType, "BENTLEY", StringComparison.OrdinalIgnoreCase)
                ? "PSWBENT"
                : wheelType;
        }

        // Canonical spelling of every known wheel/hub and module code, from the
        // identity decode tables, looked up case-insensitively.
        private static readonly Dictionary<string, string> CanonicalAttachmentCodes =
            CanonicalCodes(FanatecDeviceTables.Wheels.Values.Concat(FanatecDeviceTables.Hubs.Values));
        private static readonly Dictionary<string, string> CanonicalModuleCodes =
            CanonicalCodes(FanatecDeviceTables.Modules.Values);

        private static Dictionary<string, string> CanonicalCodes(IEnumerable<string> codes)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var code in codes)
                map[code] = code;
            return map;
        }

        /// <summary>
        /// Rewrites a profile's match codes to their canonical spelling (and the
        /// BENTLEY alias to PSWBENT). Matching to hardware ignores case, but a
        /// device's SimHub DeviceTypeID is built from these codes, so this keeps
        /// every profile for one attachment on the same ID. Codes the decode
        /// tables don't know are left as written: they can't match a wheel.
        /// </summary>
        private static void CanonicalizeMatch(WheelProfile profile)
        {
            var match = profile.Match;
            if (match == null)
                return;
            match.WheelType = Canonical(NormalizeWheelType(match.WheelType), CanonicalAttachmentCodes);
            match.ModuleType = Canonical(match.ModuleType, CanonicalModuleCodes);
        }

        private static string Canonical(string code, Dictionary<string, string> table)
        {
            return code != null && table.TryGetValue(code, out var canonical) ? canonical : code;
        }
    }
}
