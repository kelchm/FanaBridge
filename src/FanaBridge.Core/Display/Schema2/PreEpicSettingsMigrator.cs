using System;
using System.Collections.Generic;
using FanaBridge.Display.Arbitration;
using FanaBridge.Display.Legacy;
using FanaBridge.Display.Rules;
using FanaBridge.Protocol;
using Newtonsoft.Json.Linq;

namespace FanaBridge.Display.Schema2
{
    /// <summary>
    /// Spec §9b — the ONE shipped migration: pre-epic display settings → a native v2
    /// document. Bake-on-sight, marker-stamped, idempotent, and schema-independent:
    /// reads pre-epic scalars only and writes v2 POCOs. Mode-content oracle =
    /// <c>LegacyModeMigrationTests</c> (hosted base + overlay layer + trigger semantics).
    /// </summary>
    public static class PreEpicSettingsMigrator
    {
        /// <summary>Document ExtensionData key for the bake-on-sight marker.</summary>
        public const string MarkerKey = "migratedFrom";

        /// <summary>Marker value written on every successful bake.</summary>
        public const string MarkerValue = "preEpicSettings";

        /// <summary>
        /// When <paramref name="existingV2"/> is non-null, returns it unchanged (never
        /// overwrites an authored or previously baked v2 document). Otherwise bakes a
        /// fresh v2 document from pre-epic scalars and stamps <see cref="MarkerKey"/>.
        /// Never throws; unresolvable page ids omit ITM rest and log.
        /// </summary>
        public static DisplayConfigV2 Apply(
            DisplayConfigV2 existingV2,
            string displayControl,
            byte itmDefaultPage,
            byte itmDeviceId = 0,
            string displayMode = null,
            bool itmCapable = true,
            Action<string> log = null)
        {
            if (existingV2 != null)
                return existingV2;

            return Bake(
                displayControl, itmDefaultPage, itmDeviceId, displayMode, itmCapable, log);
        }

        /// <summary>
        /// Builds a new v2 document from pre-epic settings. Always stamps the marker
        /// (bake-on-sight — even when ITM rest is omitted for an unresolvable page or
        /// a segment-only device).
        /// </summary>
        /// <param name="itmCapable">
        /// When false (segment-only / basic display), no ITM rest or ITM entries are
        /// written — schema law. Mode content still bakes as hosted pages.
        /// </param>
        public static DisplayConfigV2 Bake(
            string displayControl,
            byte itmDefaultPage,
            byte itmDeviceId = 0,
            string displayMode = null,
            bool itmCapable = true,
            Action<string> log = null)
        {
            var doc = new DisplayConfigV2();
            doc.Settings = doc.Settings ?? new SettingsBlock();
            doc.Settings.Mode = MapControlToMode(displayControl);

            // §5 standing fixture: Manual is the ranked entrypoint immediately above
            // the fixed rest floor. A bake authors the row itself so serialize/clone
            // seams never temporarily lose the manual seat.
            if (doc.Priority == null)
                doc.Priority = new PriorityLadder();
            if (doc.Priority.Rows == null)
                doc.Priority.Rows = new List<PriorityRow>();
            doc.Priority.Rows.Add(new PriorityRow { Kind = PriorityRowKind.Manual });

            // §9b mode content: re-express frozen displayMode as hosted page(s).
            // Oracle = LegacyModeMigrationTests (kinds, names, overlay/trigger shape).
            PageEntry baseHosted = TrySynthesizeModeContent(doc, displayMode);

            if (itmCapable)
            {
                if (TryResolveItmPage(itmDefaultPage, itmDeviceId, out string catalogPageId))
                {
                    if (doc.Priority == null)
                        doc.Priority = new PriorityLadder();
                    if (doc.Priority.Rest == null)
                        doc.Priority.Rest = new RestBlock();

                    doc.Priority.Rest.InSessionPage = new PageRef
                    {
                        Kind = PageRefKind.ItmPage,
                        CatalogPageId = catalogPageId,
                    };
                }
                else
                {
                    SafeLog(log,
                        "PreEpicSettingsMigrator: itmDefaultPage " + itmDefaultPage
                        + " is not in this device's catalog (deviceId " + itmDeviceId
                        + ") — rest.inSessionPage omitted");
                }
            }
            else if (baseHosted != null)
            {
                // Segment-only: rest floor is the hosted base (no ITM entries at all).
                if (doc.Priority == null)
                    doc.Priority = new PriorityLadder();
                if (doc.Priority.Rest == null)
                    doc.Priority.Rest = new RestBlock();

                doc.Priority.Rest.InSessionPage = new PageRef
                {
                    Kind = PageRefKind.HostedPage,
                    Id = baseHosted.Id,
                };
            }

            StampMarker(doc);
            return doc;
        }

        /// <summary>True when the document carries the §9b bake-on-sight marker.</summary>
        public static bool HasMarker(DisplayConfigV2 doc)
        {
            if (doc?.ExtensionData == null)
                return false;
            if (!doc.ExtensionData.TryGetValue(MarkerKey, out JToken token) || token == null
                || token.Type == JTokenType.Null)
                return false;
            return string.Equals(token.Type == JTokenType.String
                    ? (string)token
                    : token.ToString(),
                MarkerValue, StringComparison.Ordinal);
        }

        /// <summary>
        /// Pre-epic <c>displayControl</c> tri-state → v2 <c>settings.mode</c>.
        /// Itm → on · Legacy → legacyOnly · Off → off · anything else → on.
        /// </summary>
        internal static SettingsMode MapControlToMode(string displayControl)
        {
            if (string.Equals(displayControl, "Legacy", StringComparison.OrdinalIgnoreCase))
                return SettingsMode.LegacyOnly;
            if (string.Equals(displayControl, "Off", StringComparison.OrdinalIgnoreCase))
                return SettingsMode.Off;
            // Itm, absent, and unknown all land on "on" (codec default for ITM-capable).
            return SettingsMode.On;
        }

        /// <summary>
        /// Resolves a pre-epic wire page number against <see cref="ItmDeviceCatalog"/>
        /// into a catalog page id (e.g. wire 1 → <c>lapInfo</c>). False when the wire
        /// is not on this device's set or has no catalog spelling.
        /// </summary>
        internal static bool TryResolveItmPage(
            byte itmDefaultPage, byte itmDeviceId, out string catalogPageId)
        {
            catalogPageId = null;
            var pages = ItmDeviceCatalog.PagesFor(itmDeviceId);
            if (pages == null)
                return false;

            for (int i = 0; i < pages.Count; i++)
            {
                var info = pages[i];
                if (info == null || info.Number != itmDefaultPage)
                    continue;

                catalogPageId = CatalogPageIdAdapter.FromItmPage(info.Page);
                return !string.IsNullOrEmpty(catalogPageId);
            }
            return false;
        }

        /// <summary>
        /// Maps frozen <c>displayMode</c> onto hosted page content. <c>None</c> → no
        /// synthesis. Unknown / null / blank → Gear (driver unknown-mode fallback).
        /// Returns the base hosted page, or null when nothing was synthesized.
        /// </summary>
        internal static PageEntry TrySynthesizeModeContent(
            DisplayConfigV2 doc, string displayMode)
        {
            if (doc == null)
                return null;

            if (string.Equals(displayMode, "None", StringComparison.OrdinalIgnoreCase))
                return null;

            if (string.Equals(displayMode, "Speed", StringComparison.OrdinalIgnoreCase))
                return AddSingleBase(doc, ContentKind.Speed, "Speed");

            if (string.Equals(displayMode, "GearAndSpeed", StringComparison.OrdinalIgnoreCase))
                return AddGearAndSpeed(doc);

            if (string.Equals(displayMode, "GearUpshiftBrackets", StringComparison.OrdinalIgnoreCase))
                return AddGearUpshiftBrackets(doc);

            // Gear, default, and unknown → Gear (driver unknown-mode fallback).
            return AddSingleBase(doc, ContentKind.Gear, "Gear");
        }

        private static PageEntry AddSingleBase(
            DisplayConfigV2 doc, ContentKind kind, string name)
        {
            var page = NewHostedPage(name, kind);
            EnsurePages(doc).Add(page);
            return page;
        }

        /// <summary>
        /// Speed base + Gear overlay layer (onChange / Gear, 2s hold).
        /// Names / kinds / ordering match LegacyModeMigrationTests GearAndSpeed.
        /// </summary>
        private static PageEntry AddGearAndSpeed(DisplayConfigV2 doc)
        {
            var page = NewHostedPage("Speed", ContentKind.Speed);
            page.Layers = new List<LayerEntry>
            {
                new LayerEntry
                {
                    Id = NewId(),
                    Name = "Gear",
                    Content = KindOnly(ContentKind.Gear),
                    Condition = new Condition
                    {
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.BuiltIn,
                            Name = BuiltInProperties.Gear,
                        },
                    },
                    Lifetime = new Lifetime
                    {
                        Kind = LifetimeKind.OnChange,
                        DurationMs = LegacyValueFormatter.GearOverlayMs,
                    },
                },
            };
            EnsurePages(doc).Add(page);
            return page;
        }

        /// <summary>
        /// Gear base + GearBrackets overlay (IsTrue / RedlineReached, whileTrue).
        /// Names / kinds / ordering match LegacyModeMigrationTests GearUpshiftBrackets.
        /// </summary>
        private static PageEntry AddGearUpshiftBrackets(DisplayConfigV2 doc)
        {
            var page = NewHostedPage("Gear", ContentKind.Gear);
            page.Layers = new List<LayerEntry>
            {
                new LayerEntry
                {
                    Id = NewId(),
                    Name = "Gear (brackets)",
                    Content = KindOnly(ContentKind.GearBrackets),
                    Condition = new Condition
                    {
                        Operator = ConditionOperator.IsTrue,
                        Source = new ValueSource
                        {
                            Kind = ValueSourceKind.BuiltIn,
                            Name = BuiltInProperties.RedlineReached,
                        },
                    },
                    Lifetime = new Lifetime
                    {
                        Kind = LifetimeKind.WhileTrue,
                    },
                },
            };
            EnsurePages(doc).Add(page);
            return page;
        }

        private static PageEntry NewHostedPage(string name, ContentKind kind)
        {
            return new PageEntry
            {
                Kind = PageEntryKind.HostedPage,
                Id = NewId(),
                Name = name,
                Base = new ContentWithEffect
                {
                    Content = KindOnly(kind),
                },
            };
        }

        private static ContentObject KindOnly(ContentKind kind)
            => new ContentObject { Kind = kind };

        private static List<PageEntry> EnsurePages(DisplayConfigV2 doc)
        {
            if (doc.Pages == null)
                doc.Pages = new List<PageEntry>();
            return doc.Pages;
        }

        private static string NewId() => Guid.NewGuid().ToString("N");

        private static void StampMarker(DisplayConfigV2 doc)
        {
            if (doc.ExtensionData == null)
                doc.ExtensionData = new Dictionary<string, JToken>(StringComparer.Ordinal);
            doc.ExtensionData[MarkerKey] = MarkerValue;
        }

        private static void SafeLog(Action<string> log, string message)
        {
            if (log == null) return;
            try { log(message); }
            catch
            {
                // Logger failures must not surface from the migrator.
            }
        }
    }
}
