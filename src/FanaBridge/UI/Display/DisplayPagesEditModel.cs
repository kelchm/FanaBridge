using System;
using System.Collections.Generic;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Twin;
using FanaBridge.Protocol;
using FanaBridge.UI.Display.Shared;

namespace FanaBridge.UI.Display
{
    /// <summary>Whether a field's source is the built-in default or a per-wheel override.
    /// No GLOBAL — profiles are deferred.</summary>
    internal enum FieldProvenance
    {
        Default,
        ThisWheel,
    }

    /// <summary>One page pill in the Pages editor strip — wire number + name from the
    /// device's <see cref="ItmPageTable"/> (not hardcoded 1..6).</summary>
    internal sealed class PagePillModel
    {
        public byte Wire { get; set; }
        public ItmPage Page { get; set; }
        public string Name { get; set; }
        public bool IsLegacy { get; set; }
        public bool IsSelected { get; set; }
    }

    /// <summary>The field inspector's pure projection for one selected param.</summary>
    internal sealed class FieldInspectorModel
    {
        public ushort ParamId { get; set; }
        public string FieldName { get; set; }
        public FieldProvenance Provenance { get; set; }
        public bool IsLocked { get; set; }
        public string SourceName { get; set; }
        public PropertyKind SourceKind { get; set; }
        public string FormatId { get; set; }
        public IReadOnlyList<Choice> FormatChoices { get; set; }
            = Array.Empty<Choice>();
        public bool HasFormatOptions { get; set; }
        public string FormatHint { get; set; }
        public string FirmwareSlotText { get; set; }
        public bool ShowResetToDefault { get; set; }
    }

    /// <summary>
    /// The testable core of the Pages &amp; fields editor: holds the working
    /// <see cref="DisplayCustomizationConfig"/> and turns every field remap / format /
    /// reset into a NEW document (immutable-after-load — FieldMappings dict copied
    /// fresh; everything else carried by reference). No SimHub or WPF — sibling of
    /// <see cref="DisplayTriggersEditModel"/>. Toggle booleans are plain primitives so
    /// the model stays free of DisplaySettings / host types.
    /// </summary>
    internal sealed class DisplayPagesEditModel
    {
        private readonly byte _itmDeviceId;
        private readonly ItmPageTable _pageTable;
        private readonly WheelCatalog _catalog;
        private readonly bool _showLapTotal;
        private readonly bool _showPositionTotal;
        private DisplayCustomizationConfig _config;
        private ItmPage _selectedPage;
        private ushort? _selectedParamId;

        public DisplayPagesEditModel(
            DisplayCustomizationConfig current,
            byte itmDeviceId,
            bool showLapTotal = true,
            bool showPositionTotal = true)
        {
            _config = current;
            _itmDeviceId = itmDeviceId;
            _showLapTotal = showLapTotal;
            _showPositionTotal = showPositionTotal;
            _pageTable = ItmPageTable.ForDevice(itmDeviceId);
            // Envelope authority for offered formats + lock (standing law §3a).
            _catalog = ResolveCatalogForDevice(itmDeviceId);
            // Land on the first non-legacy page when the table has one; else the first
            // entry (a device with only Legacy is theoretical but stay honest).
            _selectedPage = FirstEditablePage();
            _selectedParamId = FirstSelectableParam(_selectedPage);
        }

        /// <summary>The current working document (null until the first mapping is set
        /// on an empty start).</summary>
        public DisplayCustomizationConfig Config => _config;

        public byte ItmDeviceId => _itmDeviceId;

        public ItmPage SelectedPage => _selectedPage;

        public ushort? SelectedParamId => _selectedParamId;

        /// <summary>True when the selected page is the legacy / free 3-char page
        /// (delegation card, not the field inspector).</summary>
        public bool IsLegacyPage => _selectedPage == ItmPage.Legacy;

        // ── Page navigation ──────────────────────────────────────────────

        /// <summary>Page pills from the device's table, in wire order, with the active
        /// selection flag. Wire numbers and names come from the catalog — not 1..6.</summary>
        public IReadOnlyList<PagePillModel> PagePills()
        {
            var result = new List<PagePillModel>(_pageTable.Pages.Count);
            foreach (var info in _pageTable.Pages)
            {
                result.Add(new PagePillModel
                {
                    Wire = info.Number,
                    Page = info.Page,
                    Name = info.Name,
                    IsLegacy = info.IsLegacy,
                    IsSelected = info.Page == _selectedPage,
                });
            }
            return result;
        }

        /// <summary>Selects a page by content identity. Resets the selected field to
        /// the page's first remappable param (or null on Legacy / locked-only).</summary>
        public void SelectPage(ItmPage page)
        {
            if (!_pageTable.Offers(page))
                return;
            _selectedPage = page;
            _selectedParamId = FirstSelectableParam(page);
        }

        /// <summary>Selects a page by wire number (the pill's wire tag).</summary>
        public void SelectPageByWire(byte wire)
        {
            if (_pageTable.TryGetPage(wire, out var page))
                SelectPage(page);
        }

        /// <summary>Selects a field for the inspector. Accepts any param on the current
        /// page's layout (including locked Gear/EngineMapping — the inspector shows the
        /// lock state). No-op when the param is not on this page.</summary>
        public void SelectParam(ushort paramId)
        {
            if (!PageCarries(paramId, _selectedPage) && !IsCenterParam(paramId))
                return;
            _selectedParamId = paramId;
        }

        // ── Inspector projection ─────────────────────────────────────────

        /// <summary>The inspector card for the selected field, or null when nothing is
        /// selected (Legacy page, or empty selection).</summary>
        public FieldInspectorModel Inspector()
        {
            if (!_selectedParamId.HasValue)
                return null;
            return BuildInspector(_selectedParamId.Value);
        }

        /// <summary>Provenance for a param: THIS WHEEL when a FieldMapping is present
        /// (source and/or format), DEFAULT otherwise. No GLOBAL.</summary>
        public FieldProvenance ProvenanceOf(ushort paramId)
            => HasMapping(paramId) ? FieldProvenance.ThisWheel : FieldProvenance.Default;

        /// <summary>Whether a FieldMapping entry exists for the param.</summary>
        public bool HasMapping(ushort paramId)
        {
            var map = _config?.FieldMappings;
            return map != null && map.ContainsKey(paramId);
        }

        /// <summary>The format choices for a param (empty when the envelope offers none).
        /// Labels are UI-facing; ids are the <see cref="FieldFormats"/> keys.
        /// When <paramref name="catalog"/> is null, falls back to format-family tables
        /// only (standing law: envelope DATA when a catalog resolves).</summary>
        public static IReadOnlyList<Choice> FormatChoicesFor(
            ushort paramId, WheelCatalog catalog = null)
        {
            var allowed = FieldEnvelope.OfferedFormats(catalog, paramId);
            if (allowed.Count == 0)
                return Array.Empty<Choice>();
            var list = new Choice[allowed.Count];
            for (int i = 0; i < allowed.Count; i++)
                list[i] = new Choice(allowed[i], FormatLabel(allowed[i]));
            return list;
        }

        /// <summary>The effective format id to show as selected — same precedence as
        /// the ITM mapper (explicit &gt; override-bare &gt; Show*Total toggle &gt;
        /// family default). Null when the param has no format options.</summary>
        public string EffectiveFormatId(ushort paramId)
        {
            if (FieldEnvelope.OfferedFormats(_catalog, paramId).Count == 0)
                return null;
            string explicitFormat = null;
            bool hasMapping = false;
            if (_config?.FieldMappings != null
                && _config.FieldMappings.TryGetValue(paramId, out var mapping))
            {
                hasMapping = true;
                explicitFormat = mapping?.Format;
                // Drop unknown format text the same way the validator would — fall
                // through to the rest of the chain rather than surfacing junk.
                if (!string.IsNullOrEmpty(explicitFormat)
                    && !FieldEnvelope.IsFormatAllowed(_catalog, paramId, explicitFormat))
                    explicitFormat = null;
            }
            return FieldFormats.EffectiveFormat(
                paramId,
                explicitFormat,
                hasMapping,
                _showLapTotal,
                _showPositionTotal);
        }

        /// <summary>Whether a format choice for <paramref name="paramId"/> should also
        /// write <c>ItmShowLapTotal</c> / <c>ItmShowPositionTotal</c> (one-release
        /// downgrade mirror — same pattern as the retired itmEnabled checkbox).</summary>
        public static bool FormatMirrorsShowTotal(ushort paramId)
            => paramId == ItmParam.Lap || paramId == ItmParam.Position;

        /// <summary>Post-mirror Show*Total value for a Lap/Position format choice
        /// (<c>true</c> when <paramref name="format"/> is <see cref="FieldFormats.WithTotal"/>).</summary>
        public static bool ShowTotalFromFormat(string format)
            => string.Equals(format, FieldFormats.WithTotal, StringComparison.Ordinal);

        /// <summary>The built-in default source for a param (what DEFAULT shows), or
        /// null when unknown / locked.</summary>
        public static PropertySpec DefaultSource(ushort paramId)
        {
            string name = DefaultBuiltInName(paramId);
            if (name == null)
                return null;
            return new PropertySpec { Kind = PropertyKind.BuiltIn, Name = name };
        }

        /// <summary>Display name for a param in the inspector header (layout label,
        /// colon stripped, or a short fallback).</summary>
        public static string FieldDisplayName(ushort paramId)
        {
            string fromLayout = LayoutLabelFor(paramId);
            if (!string.IsNullOrEmpty(fromLayout))
                return StripLabelDecor(fromLayout);
            return FallbackParamName(paramId);
        }

        // ── Mutations (each returns the NEW document) ────────────────────

        /// <summary>Sets the source for a param (SimHub pick or built-in). Creates a
        /// FieldMapping when none exists; keeps an existing Format when present. When
        /// the picked source is the param's exact default and there is no non-default
        /// format, the mapping is dropped (no-op override — registry path, byte-identical
        /// by construction). Fresh FieldMappings dict; other document members by
        /// reference. Envelope lock (<c>overridable:false</c>) rejects the write.</summary>
        public DisplayCustomizationConfig SetSource(ushort paramId, PropertyKind kind,
            string name)
        {
            if (FieldEnvelope.IsLocked(_catalog, paramId))
                return _config;
            if (string.IsNullOrEmpty(name))
                return _config;

            var mappings = CopyMappings();
            mappings.TryGetValue(paramId, out var existing);
            var source = new PropertySpec
            {
                Kind = kind,
                Name = name,
                ExtensionData = existing?.Source?.ExtensionData,
            };
            string format = existing?.Format;

            // Default source + no non-default format → drop (same prune as SetFormat).
            if (IsDefaultSource(paramId, source)
                && (string.IsNullOrEmpty(format) || IsToggleAwareDefaultFormat(paramId, format)))
            {
                mappings.Remove(paramId);
                return Commit(mappings);
            }

            mappings[paramId] = new FieldMapping
            {
                Source = source,
                Format = format,
                ExtensionData = existing?.ExtensionData,
            };
            return Commit(mappings);
        }

        /// <summary>Sets the format key for a param. Rejects unknown / disallowed
        /// formats. A format-only override still carries the built-in default source
        /// (validator requires a source body); when the format equals the toggle-aware
        /// default and the source is still the built-in, the mapping is dropped entirely
        /// (back to DEFAULT provenance). Lap/Position format choices are mirrored to
        /// Show*Total by the view — the prune anticipates that post-mirror default so a
        /// chosen format always equals the new toggle default and prunes.
        /// Envelope lock and offered-format set are DATA-driven.</summary>
        public DisplayCustomizationConfig SetFormat(ushort paramId, string format)
        {
            if (FieldEnvelope.IsLocked(_catalog, paramId))
                return _config;
            if (!FieldEnvelope.IsFormatAllowed(_catalog, paramId, format))
                return _config;

            var mappings = CopyMappings();
            mappings.TryGetValue(paramId, out var existing);
            var source = existing?.Source ?? DefaultSource(paramId);
            bool sourceIsDefault = IsDefaultSource(paramId, source);

            // Format-only at the toggle-aware default with no real source override → drop.
            // Lap/Position: view will mirror format → Show*Total, so compare against the
            // post-mirror default (chosen format becomes the toggle default by construction).
            if (sourceIsDefault && IsToggleAwareDefaultFormat(paramId, format, anticipateMirror: true))
            {
                mappings.Remove(paramId);
                return Commit(mappings);
            }

            mappings[paramId] = new FieldMapping
            {
                Source = source,
                Format = format,
                ExtensionData = existing?.ExtensionData,
            };
            return Commit(mappings);
        }

        /// <summary>Removes the FieldMapping for a param ("Reset to default"). No-op
        /// when none is present.</summary>
        public DisplayCustomizationConfig ResetToDefault(ushort paramId)
        {
            var map = _config?.FieldMappings;
            if (map == null || !map.ContainsKey(paramId))
                return _config;
            var mappings = CopyMappings();
            mappings.Remove(paramId);
            return Commit(mappings);
        }

        // ── Internals ────────────────────────────────────────────────────

        private FieldInspectorModel BuildInspector(ushort paramId)
        {
            // Standing law: lock + offered formats from catalog envelope DATA.
            bool locked = FieldEnvelope.IsLocked(_catalog, paramId);
            var provenance = ProvenanceOf(paramId);
            string sourceName;
            PropertyKind sourceKind;
            if (!locked && _config?.FieldMappings != null
                && _config.FieldMappings.TryGetValue(paramId, out var mapping)
                && mapping?.Source != null
                && !string.IsNullOrEmpty(mapping.Source.Name))
            {
                sourceName = mapping.Source.Name;
                sourceKind = mapping.Source.Kind;
            }
            else
            {
                var def = DefaultSource(paramId);
                sourceName = def?.Name;
                sourceKind = def?.Kind ?? PropertyKind.BuiltIn;
            }

            var formats = FormatChoicesFor(paramId, _catalog);
            bool hasFormats = formats.Count > 0;
            string formatHint = null;
            if (!hasFormats)
                formatHint = "No unit/format options for this field.";
            else if (HasSourceOverride(paramId))
                formatHint = "Source override: totals/units default to bare unless set explicitly.";

            return new FieldInspectorModel
            {
                ParamId = paramId,
                FieldName = FieldDisplayName(paramId),
                Provenance = provenance,
                IsLocked = locked,
                SourceName = sourceName ?? FallbackParamName(paramId),
                SourceKind = sourceKind,
                FormatId = EffectiveFormatId(paramId),
                FormatChoices = formats,
                HasFormatOptions = hasFormats,
                FormatHint = formatHint,
                FirmwareSlotText = FallbackParamName(paramId) + "  ·  " + paramId,
                ShowResetToDefault = !locked && provenance == FieldProvenance.ThisWheel,
            };
        }

        private bool HasSourceOverride(ushort paramId)
        {
            if (_config?.FieldMappings == null)
                return false;
            if (!_config.FieldMappings.TryGetValue(paramId, out var mapping))
                return false;
            return mapping?.Source != null && !string.IsNullOrEmpty(mapping.Source.Name);
        }

        private Dictionary<ushort, FieldMapping> CopyMappings()
        {
            var copy = new Dictionary<ushort, FieldMapping>();
            var src = _config?.FieldMappings;
            if (src == null)
                return copy;
            foreach (var kv in src)
                copy[kv.Key] = kv.Value;   // FieldMapping instances are not mutated
            return copy;
        }

        // Fresh document: FieldMappings is the new dict; everything else by reference
        // (same pattern as DisplayTriggersEditModel.Commit).
        private DisplayCustomizationConfig Commit(Dictionary<ushort, FieldMapping> mappings)
        {
            var src = _config;
            var cfg = new DisplayCustomizationConfig
            {
                SchemaVersion = src?.SchemaVersion ?? DisplayCustomizationConfig.CurrentSchemaVersion,
                ProfileId = src?.ProfileId,
                Itm = src?.Itm ?? new ItmRuleSet(),
                Legacy = src?.Legacy ?? new LegacyRuleSet(),
                FieldMappings = mappings,
                ExtensionData = src?.ExtensionData,
            };
            _config = cfg;
            return cfg;
        }

        /// <summary>
        /// Resolve the shipped catalog whose declared deviceId matches
        /// <paramref name="itmDeviceId"/>, or null when none (no-catalog fallback).
        /// </summary>
        internal static WheelCatalog ResolveCatalogForDevice(byte itmDeviceId)
        {
            foreach (var kv in CatalogLoader.LoadShipped())
            {
                byte? declared = CatalogLoader.ReadDeclaredDeviceId(kv.Value);
                if (declared.HasValue && declared.Value == itmDeviceId)
                    return kv.Value;
            }
            return null;
        }

        private ItmPage FirstEditablePage()
        {
            foreach (var info in _pageTable.Pages)
                if (!info.IsLegacy)
                    return info.Page;
            return _pageTable.Pages.Count > 0 ? _pageTable.Pages[0].Page : ItmPage.LapInfo;
        }

        private static ushort? FirstSelectableParam(ItmPage page)
        {
            if (page == ItmPage.Legacy)
                return null;
            var layout = ItmDisplayLayout.For(page);
            if (!layout.HasSlots)
                return null;
            // Prefer the first field so the inspector is useful on open.
            foreach (var pos in new[]
            {
                ItmSlotPosition.LeftTop, ItmSlotPosition.LeftBottom,
                ItmSlotPosition.RightTop, ItmSlotPosition.RightBottom,
            })
            {
                var slot = layout.SlotAt(pos);
                if (slot == null) continue;
                foreach (var f in slot.Fields)
                    return f.ParamId;
            }
            return null;
        }

        private static bool PageCarries(ushort paramId, ItmPage page)
        {
            foreach (var id in ItmTelemetry.ParamsFor(page))
                if (id == paramId)
                    return true;
            return false;
        }

        // Center-zone Speed/Gear appear on every telemetry page but are not in the
        // four field slots of the values snapshot — still selectable via future hit
        // regions; keep the door open without inventing layout slots.
        private static bool IsCenterParam(ushort paramId)
            => paramId == ItmParam.Speed || paramId == ItmParam.Gear;

        /// <summary>Whether <paramref name="format"/> equals the no-mapping default
        /// for <paramref name="paramId"/> (toggle-aware for Lap/Position; family
        /// default for Fuel/temps). When <paramref name="anticipateMirror"/> is true
        /// and the param is Lap/Position, uses the post-mirror Show*Total that the
        /// view will write for this format choice.</summary>
        private bool IsToggleAwareDefaultFormat(ushort paramId, string format,
            bool anticipateMirror = false)
        {
            bool showLap = _showLapTotal;
            bool showPos = _showPositionTotal;
            if (anticipateMirror && FormatMirrorsShowTotal(paramId))
            {
                bool withTotal = ShowTotalFromFormat(format);
                if (paramId == ItmParam.Lap)
                    showLap = withTotal;
                else if (paramId == ItmParam.Position)
                    showPos = withTotal;
            }
            string defaultsTo = FieldFormats.EffectiveFormat(
                paramId, null, false, showLap, showPos);
            return string.Equals(defaultsTo, format, StringComparison.Ordinal);
        }

        private static bool IsDefaultSource(ushort paramId, PropertySpec source)
        {
            if (source == null || string.IsNullOrEmpty(source.Name))
                return true;
            var def = DefaultSource(paramId);
            if (def == null)
                return false;
            return source.Kind == PropertyKind.BuiltIn
                && string.Equals(source.Name, def.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatLabel(string format)
        {
            switch (format)
            {
                case FieldFormats.WithTotal: return "With total";
                case FieldFormats.Bare: return "Value only";
                case FieldFormats.Unit: return "With unit";
                case FieldFormats.Neutral: return "Neutral for blank";
                case FieldFormats.Blank: return "Blank for empty";
                case FieldFormats.Whole: return "Whole number";
                case FieldFormats.OneDecimal: return "One decimal";
                default: return format;
            }
        }

        private static string LayoutLabelFor(ushort paramId)
        {
            foreach (ItmPage page in Enum.GetValues(typeof(ItmPage)))
            {
                var layout = ItmDisplayLayout.For(page);
                if (!layout.HasSlots) continue;
                foreach (var pos in new[]
                {
                    ItmSlotPosition.LeftTop, ItmSlotPosition.LeftBottom,
                    ItmSlotPosition.RightTop, ItmSlotPosition.RightBottom,
                })
                {
                    var slot = layout.SlotAt(pos);
                    if (slot == null) continue;
                    foreach (var f in slot.Fields)
                    {
                        if (f.ParamId != paramId) continue;
                        if (!string.IsNullOrEmpty(f.Label))
                            return f.Label;
                        if (!string.IsNullOrEmpty(slot.Label))
                            return slot.Label;
                    }
                }
            }
            return null;
        }

        private static string StripLabelDecor(string label)
        {
            if (string.IsNullOrEmpty(label))
                return label;
            // "LAPS:" → "Laps"; "DRS: ZONE / ACTIVE" kept informative.
            string t = label.Trim();
            if (t.EndsWith(":", StringComparison.Ordinal))
                t = t.Substring(0, t.Length - 1).TrimEnd();
            // Title-case all-caps firmware labels for the inspector header.
            if (IsAllCaps(t) && t.IndexOf(' ') < 0 && t.IndexOf('/') < 0)
                return char.ToUpperInvariant(t[0]) + t.Substring(1).ToLowerInvariant();
            if (IsAllCaps(t))
            {
                var parts = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < parts.Length; i++)
                {
                    var p = parts[i];
                    if (p.Length == 0) continue;
                    parts[i] = char.ToUpperInvariant(p[0])
                        + (p.Length > 1 ? p.Substring(1).ToLowerInvariant() : "");
                }
                return string.Join(" ", parts);
            }
            return t;
        }

        private static bool IsAllCaps(string s)
        {
            bool anyLetter = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsLetter(c))
                {
                    anyLetter = true;
                    if (char.IsLower(c))
                        return false;
                }
            }
            return anyLetter;
        }

        private static string DefaultBuiltInName(ushort paramId)
        {
            switch (paramId)
            {
                case ItmParam.Speed: return BuiltInProperties.Speed;
                case ItmParam.Gear: return BuiltInProperties.Gear;
                case ItmParam.Lap: return BuiltInProperties.CurrentLap;
                case ItmParam.Position: return BuiltInProperties.Position;
                case ItmParam.LapTime: return BuiltInProperties.CurrentLapTime;
                case ItmParam.LastLapTime: return BuiltInProperties.LastLapTime;
                case ItmParam.Fuel: return BuiltInProperties.Fuel;
                case ItmParam.ErsLevel: return BuiltInProperties.ErsPercent;
                case ItmParam.DrsZone: return BuiltInProperties.DrsAvailable;
                case ItmParam.DrsActive: return BuiltInProperties.DrsEnabled;
                case ItmParam.DeltaOwnBest: return BuiltInProperties.DeltaToSessionBest;
                case ItmParam.TcSetting: return BuiltInProperties.TcLevel;
                case ItmParam.AbsSetting: return BuiltInProperties.AbsLevel;
                case ItmParam.EngineMapping: return BuiltInProperties.EngineMap;
                case ItmParam.OilTemp: return BuiltInProperties.OilTemperature;
                case ItmParam.BrakeBias: return BuiltInProperties.BrakeBias;
                case ItmParam.BestLapTime: return BuiltInProperties.BestLapTime;
                case ItmParam.CarAhead: return BuiltInProperties.GapAhead;
                case ItmParam.CarBehind: return BuiltInProperties.GapBehind;
                case ItmParam.TyreFlTemp: return BuiltInProperties.TyreTempFrontLeft;
                case ItmParam.TyreFrTemp: return BuiltInProperties.TyreTempFrontRight;
                case ItmParam.TyreRlTemp: return BuiltInProperties.TyreTempRearLeft;
                case ItmParam.TyreRrTemp: return BuiltInProperties.TyreTempRearRight;
                default: return null;
            }
        }

        private static string FallbackParamName(ushort paramId)
        {
            switch (paramId)
            {
                case ItmParam.Speed: return "Speed";
                case ItmParam.Gear: return "Gear";
                case ItmParam.Lap: return "Lap";
                case ItmParam.Position: return "Position";
                case ItmParam.LapTime: return "Lap time";
                case ItmParam.LastLapTime: return "Last lap";
                case ItmParam.Fuel: return "Fuel";
                case ItmParam.ErsLevel: return "ERS";
                case ItmParam.DrsZone: return "DRS zone";
                case ItmParam.DrsActive: return "DRS active";
                case ItmParam.DeltaOwnBest: return "Delta";
                case ItmParam.TcSetting: return "TC";
                case ItmParam.AbsSetting: return "ABS";
                case ItmParam.EngineMapping: return "Engine map";
                case ItmParam.OilTemp: return "Oil temp";
                case ItmParam.BrakeBias: return "Brake bias";
                case ItmParam.BestLapTime: return "Best lap";
                case ItmParam.CarAhead: return "Car ahead";
                case ItmParam.CarBehind: return "Car behind";
                case ItmParam.TyreFlTemp: return "FL tire temp";
                case ItmParam.TyreFrTemp: return "FR tire temp";
                case ItmParam.TyreRlTemp: return "RL tire temp";
                case ItmParam.TyreRrTemp: return "RR tire temp";
                default: return "Param " + paramId;
            }
        }
    }
}
