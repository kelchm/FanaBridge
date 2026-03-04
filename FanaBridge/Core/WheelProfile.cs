using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace FanaBridge
{
    // ── JSON-serializable model ──────────────────────────────────────────

    /// <summary>
    /// Indicates whether a profile was shipped with the plugin or created by the user.
    /// </summary>
    public enum ProfileSource
    {
        /// <summary>Embedded in the plugin assembly — immutable.</summary>
        BuiltIn,
        /// <summary>Loaded from the user profile directory on disk.</summary>
        User,
    }

    /// <summary>
    /// Pixel encoding for the Color LED channel (subcmd 0x02).
    /// Most Fanatec hardware uses standard RGB565 (5-6-5 bit layout),
    /// but some modules only read 5 green bits (RGB555).
    /// </summary>
    public enum ColorFormat
    {
        /// <summary>Standard 16-bit: 5 red, 6 green, 5 blue.</summary>
        Rgb565,
        /// <summary>5-5-5 green: only 5 green bits are read by the LED controller.
        /// The MSB of the 6-bit green field is ignored/misinterpreted.</summary>
        Rgb555,
    }

    /// <summary>
    /// Hardware communication channel for a single LED.
    /// Determines which col03 sub-command and encoding is used.
    /// </summary>
    public enum LedChannel
    {
        /// <summary>subcmd 0x00 — full RGB565 (Rev/RPM LEDs).</summary>
        Rev,
        /// <summary>subcmd 0x01 — full RGB565 (Flag/status LEDs).</summary>
        Flag,
        /// <summary>subcmd 0x02 — full RGB565 (button-area color LEDs).</summary>
        Color,
        /// <summary>subcmd 0x03 — 3-bit intensity only (monochrome LEDs).</summary>
        Mono,
    }

    /// <summary>
    /// Semantic role of a single LED.  Drives SimHub categorization
    /// and can be used for future ASTR auto-generation.
    /// </summary>
    public enum LedRole
    {
        /// <summary>RPM/shift indicator.</summary>
        Rev,
        /// <summary>Status/warning flag indicator.</summary>
        Flag,
        /// <summary>Button back-light.</summary>
        Button,
        /// <summary>Encoder knob indicator.</summary>
        Encoder,
        /// <summary>General-purpose indicator (not tied to an input).</summary>
        Indicator,
    }

    /// <summary>
    /// Describes a single physical LED on the device.
    /// The array order in the profile defines the SimHub logical index.
    /// </summary>
    public class LedDefinition
    {
        /// <summary>Hardware communication channel.</summary>
        [JsonProperty("channel")]
        public LedChannel Channel { get; set; }

        /// <summary>
        /// Index within the channel's protocol array.
        /// For <see cref="LedChannel.Color"/>: slot in the subcmd 0x02 color array (0-11).
        /// For <see cref="LedChannel.Mono"/>: byte index in the 16-byte intensity payload.
        /// For <see cref="LedChannel.Rev"/>/<see cref="LedChannel.Flag"/>: slot in subcmd 0x00/0x01.
        /// </summary>
        [JsonProperty("hwIndex")]
        public int HwIndex { get; set; }

        /// <summary>Semantic role — what kind of LED this is.</summary>
        [JsonProperty("role")]
        public LedRole Role { get; set; }

        /// <summary>Human-readable label for UI display.</summary>
        [JsonProperty("label")]
        public string Label { get; set; }

        /// <summary>
        /// Optional associated input name (e.g. "enc_left").
        /// Used for future ASTR profile auto-generation.
        /// </summary>
        [JsonProperty("input", NullValueHandling = NullValueHandling.Ignore)]
        public string Input { get; set; }
    }

    /// <summary>
    /// Matching criteria to associate a profile with the connected hardware.
    /// Matches against SDK-reported wheel type and optional module type.
    /// </summary>
    public class ProfileMatch
    {
        /// <summary>
        /// SDK wheel type short code (e.g. "PSWBMW", "PHUB").
        /// Matched against the SDK enum name with "FS_WHEEL_SWTYPE_" stripped.
        /// </summary>
        [JsonProperty("wheelType")]
        public string WheelType { get; set; }

        /// <summary>
        /// Optional SDK module type short code (e.g. "PBMR", "PBME").
        /// Matched against the SDK enum with "FS_WHEEL_SW_MODULETYPE_" stripped.
        /// Null for standalone wheels (no module).
        /// </summary>
        [JsonProperty("moduleType", NullValueHandling = NullValueHandling.Ignore)]
        public string ModuleType { get; set; }
    }

    /// <summary>
    /// A complete wheel profile — the single source of truth for a device's
    /// LED and display configuration.  Loaded from JSON files in the Profiles
    /// directory.  Can be shipped with the plugin (built-in) or created by
    /// users for unsupported wheels.
    /// </summary>
    public class WheelProfile
    {
        /// <summary>Unique profile identifier (e.g. "PSWBMW", "PHUB_PBMR").</summary>
        [JsonProperty("id")]
        public string Id { get; set; }

        /// <summary>Full product name.</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>Short display name for SimHub UI.</summary>
        [JsonProperty("shortName")]
        public string ShortName { get; set; }

        /// <summary>Matching criteria for auto-detection via SDK.</summary>
        [JsonProperty("match")]
        public ProfileMatch Match { get; set; }

        /// <summary>Display type: "None", "Basic", or "Itm".</summary>
        [JsonProperty("display")]
        public string Display { get; set; }

        /// <summary>
        /// Pixel encoding for the Color LED channel.
        /// Defaults to "Rgb565" when omitted.  Set to "Rgb555" for hardware
        /// whose LED controller only reads 5 green bits (e.g. Button Module Rally).
        /// </summary>
        [JsonProperty("colorFormat", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string ColorFormatRaw { get; set; }

        /// <summary>Parsed color format enum (defaults to Rgb565).</summary>
        [JsonIgnore]
        public ColorFormat ColorFormat
        {
            get
            {
                if (Enum.TryParse(ColorFormatRaw, true, out ColorFormat cf))
                    return cf;
                return ColorFormat.Rgb565;
            }
        }

        /// <summary>
        /// Ordered array of LED definitions.  Array index = SimHub logical index.
        /// The driver iterates this list each frame and dispatches to the
        /// appropriate hardware channel based on <see cref="LedDefinition.Channel"/>.
        /// </summary>
        [JsonProperty("leds")]
        public List<LedDefinition> Leds { get; set; } = new List<LedDefinition>();

        // ── Computed views ───────────────────────────────────────────────

        /// <summary>Parsed display type enum.</summary>
        [JsonIgnore]
        public DisplayType DisplayType
        {
            get
            {
                if (Enum.TryParse(Display, true, out DisplayType dt))
                    return dt;
                return FanaBridge.DisplayType.None;
            }
        }

        /// <summary>Total LED count across all channels.</summary>
        [JsonIgnore]
        public int TotalLedCount => Leds.Count;

        /// <summary>Count of Rev LEDs (subcmd 0x00).</summary>
        [JsonIgnore]
        public int RevLedCount => Leds.Count(l => l.Channel == LedChannel.Rev);

        /// <summary>Count of Flag LEDs (subcmd 0x01).</summary>
        [JsonIgnore]
        public int FlagLedCount => Leds.Count(l => l.Channel == LedChannel.Flag);

        /// <summary>Count of RGB color LEDs (subcmd 0x02).</summary>
        [JsonIgnore]
        public int ColorLedCount => Leds.Count(l => l.Channel == LedChannel.Color);

        /// <summary>Count of monochrome intensity LEDs (subcmd 0x03).</summary>
        [JsonIgnore]
        public int MonoLedCount => Leds.Count(l => l.Channel == LedChannel.Mono);

        /// <summary>Count of "button" LEDs for SimHub (color + mono = everything except rev/flag).</summary>
        [JsonIgnore]
        public int ButtonLedCount => ColorLedCount + MonoLedCount;

        /// <summary>Rev + Flag count (for SimHub's LedCount in LedModuleOptions).</summary>
        [JsonIgnore]
        public int RevFlagCount => RevLedCount + FlagLedCount;

        /// <summary>True if this device has any LEDs at all.</summary>
        [JsonIgnore]
        public bool HasLeds => Leds.Count > 0;

        // ── Runtime metadata (set by WheelProfileStore, not serialized) ──

        /// <summary>
        /// Whether this profile is built-in (embedded resource) or user-created.
        /// Set by <see cref="WheelProfileStore"/> during loading.
        /// </summary>
        [JsonIgnore]
        public ProfileSource Source { get; set; }

        /// <summary>
        /// Disk path for user profiles, or the embedded resource name for
        /// built-in profiles.  Useful for diagnostics and UI display.
        /// </summary>
        [JsonIgnore]
        public string SourcePath { get; set; }
    }

    // ── Profile loader ───────────────────────────────────────────────────

    /// <summary>
    /// Loads <see cref="WheelProfile"/> definitions and provides lookup by
    /// SDK wheel/module type.
    ///
    /// Loading order (later entries override earlier ones with the same ID):
    ///   1. Built-in profiles embedded in the plugin DLL as assembly resources.
    ///      These are immutable and cannot be modified by users.
    ///   2. User profiles from a writable directory on disk (future).
    ///      Users can create/edit these to support new wheels.
    /// </summary>
    public static class WheelProfileStore
    {
        private static readonly Dictionary<string, WheelProfile> _byId
            = new Dictionary<string, WheelProfile>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;

        /// <summary>
        /// Loads all profiles from embedded resources and user directory.
        /// Safe to call multiple times — subsequent calls are no-ops.
        /// </summary>
        public static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            // 1. Built-in profiles: embedded in the assembly (immutable)
            LoadFromEmbeddedResources();

            // 2. User profiles: writable directory on disk (overrides built-in)
            string userDir = GetUserProfileDirectory();
            if (userDir != null)
                LoadFromDirectory(userDir);

            SimHub.Logging.Current.Info(
                "WheelProfileStore: Loaded " + _byId.Count + " profile(s)");
        }

        /// <summary>
        /// Reloads all profiles from scratch (embedded + user directory).
        /// Called after the wizard saves a new profile so the change takes
        /// effect immediately without restarting SimHub.
        /// </summary>
        public static void Reload()
        {
            _byId.Clear();
            _loaded = false;
            EnsureLoaded();
            SimHub.Logging.Current.Info(
                "WheelProfileStore: Reloaded — " + _byId.Count + " profile(s)");
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
        private static void LoadFromEmbeddedResources()
        {
            var assembly = typeof(WheelProfileStore).Assembly;

            foreach (string resourceName in assembly.GetManifestResourceNames())
            {
                // Embedded resource names follow: {RootNamespace}.{folder}.{filename}
                // e.g. "FanaBridge.Profiles.bmw-m4-gt3.json"
                if (!resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (resourceName.IndexOf(".Profiles.", StringComparison.OrdinalIgnoreCase) < 0)
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

                            _byId[profile.Id] = profile;
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
        private static void LoadFromDirectory(string directory)
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

                    bool wasBuiltIn = _byId.TryGetValue(profile.Id, out var existing)
                        && existing.Source == ProfileSource.BuiltIn;
                    bool wasUser = _byId.TryGetValue(profile.Id, out var existingUser)
                        && existingUser.Source == ProfileSource.User;

                    _byId[profile.Id] = profile;

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
        /// Looks up a profile by its ID.
        /// </summary>
        public static WheelProfile GetById(string id)
        {
            EnsureLoaded();
            return _byId.TryGetValue(id, out var profile) ? profile : null;
        }

        /// <summary>
        /// Finds the best matching profile for the given SDK wheel/module type.
        /// For hub+module combos, tries "{wheelType}_{moduleType}" first,
        /// then falls back to "{wheelType}" alone.
        /// Returns null if no profile matches.
        /// </summary>
        public static WheelProfile FindByWheelType(string wheelType, string moduleType = null)
        {
            EnsureLoaded();

            // Try compound key first (hub + module)
            if (!string.IsNullOrEmpty(moduleType))
            {
                string compoundId = wheelType + "_" + moduleType;
                if (_byId.TryGetValue(compoundId, out var compound))
                    return compound;
            }

            // Fall back to wheel-only key
            if (_byId.TryGetValue(wheelType, out var profile))
                return profile;

            // Scan by match criteria (for profiles that use non-standard IDs)
            foreach (var p in _byId.Values)
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
            EnsureLoaded();
            return _byId.Values;
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
