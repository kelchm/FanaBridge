using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Schema2;
using Newtonsoft.Json;
using Xunit;

namespace FanaBridge.Tests.Display
{
    /// <summary>
    /// S-style freeze pin for Schema2 + Catalog JSON keys: reflects over every public
    /// class in those namespaces, collects every <see cref="JsonPropertyAttribute"/>
    /// name (type → sorted member names), and asserts the deterministic inventory
    /// string. ANY key rename/addition/removal breaks this test; the failure message
    /// prints the full actual inventory so a sanctioned additive change is a one-string
    /// update of <see cref="FrozenInventory"/>.
    /// </summary>
    public class Schema2FrozenKeyInventoryTests
    {
        /// <summary>
        /// Freeze artifact. Update only when a schema/catalog key change is intentional.
        /// </summary>
        private static readonly string FrozenInventory = string.Join("\n", new[]
        {
            "ChildRef: field, layerId, overrideId, pageId",
            "Condition: hysteresis, operator, source, value",
            "ContentObject: format, kind, source, text",
            "ContentWithEffect: content, effect",
            "CycleEntry: id, members, name, periodMs",
            "DisplayConfigV2: cycles, fields, pageOrder, pages, playlists, priority, profileId, schemaVersion, settings, sharedFields, wheelScreen",
            "FieldBase: baseSuffix, format, source",
            "FieldEntry: base, overrides",
            "FieldOverride: actsAsEntrypoint, alignment, condition, content, effect, enabled, id, lifetime, runs, writes",
            "IdleSpec: kind, page, playlist, screen",
            "LayerEntry: actsAsEntrypoint, condition, content, effect, enabled, id, lifetime, name, runs",
            "Lifetime: direction, durationMs, kind, then",
            "PageEntry: base, catalogPageId, id, kind, layers, name, nameOverride, removed",
            "PageRef: catalogPageId, id, kind",
            "PlaylistEntry: id, name, steps, terminal",
            "PlaylistStep: destination, durationMs",
            "PriorityLadder: rest, rows",
            "PriorityRow: bringUpLifetime, childRef, id, kind, lifetime, returnToRestAfterMs, summons, target",
            "RestBlock: idle, inSessionPage",
            "SettingsBlock: mode, rejectUncommandedChanges",
            "Summon: condition, enabled, id, lifetime, name, runs",
            "ValueSource: kind, name",
            "WheelScreenPlane: rules",
            "WheelScreenRule: condition, enabled, id, lifetime, name, runs, screen",
            "AliasEntry: alias, kind, notes, ref, unit",
            "AliasPatternRule: aliasPattern, match, notes, unit",
            "AliasPrefixRule: aliasPattern, notes, prefix, unit",
            "AliasTable: aliasTableVersion, aliases, patternRules, prefixRules",
            "AnnouncedFormats: byParam, provisional",
            "CatalogFieldDefinition: displayLabel, firmwareLabel, header, id, overridable, paramId, provisional, shortCode, suffix, value",
            "CatalogFieldPlacement: field, primaryHost, region",
            "CatalogPage: id, index, name, placements, provisional",
            "CatalogTransitions: legacyEntryMs, legacyExitMs, provisional, virtualRepaintMs",
            "FieldRegion: column, row, shared",
            "FieldSuffixCapability: provisional, supported, width",
            "FieldValueCapability: ascii, numeric",
            "ItmCatalogSection: fields, legacyPageIndex, pages, transitions",
            "ScreenCommandsCapability: blank, logo, logoInverted, provisional, white",
            "SegmentCatalogSection: blink, charTable, decimalPerDigit, present",
            "WheelCatalog: announcedFormats, catalogVersion, displayName, itm, provisional, screenCommands, segment, wheelId",
        });

        [Fact]
        public void Schema2AndCatalog_JsonPropertyInventory_IsFrozen()
        {
            string actual = BuildInventory();
            if (!string.Equals(FrozenInventory, actual, StringComparison.Ordinal))
            {
                Assert.Fail(
                    "Schema2/Catalog JsonProperty inventory drift.\n"
                    + "If this is a sanctioned additive/rename change, replace FrozenInventory with:\n\n"
                    + actual
                    + "\n");
            }
        }

        /// <summary>
        /// Reflects over every public class in Schema2 and Catalog, collects
        /// [JsonProperty] names, renders deterministically (type name → sorted keys).
        /// </summary>
        public static string BuildInventory()
        {
            var assembly = typeof(DisplayConfigV2).Assembly;
            // Anchor Catalog so the assembly stays linked when tests trim.
            _ = typeof(WheelCatalog);

            var namespaces = new[]
            {
                "FanaBridge.Display.Schema2",
                "FanaBridge.Display.Catalog",
            };

            var lines = new List<string>();
            foreach (string ns in namespaces)
            {
                var types = assembly.GetTypes()
                    .Where(t => t.IsClass
                        && t.IsPublic
                        && !t.IsAbstract
                        && t.Namespace == ns)
                    .OrderBy(t => t.Name, StringComparer.Ordinal);

                foreach (var type in types)
                {
                    var names = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Select(p =>
                        {
                            var attr = p.GetCustomAttribute<JsonPropertyAttribute>();
                            if (attr == null)
                                return null;
                            // Explicit PropertyName when set; otherwise the CLR name as authored.
                            return string.IsNullOrEmpty(attr.PropertyName)
                                ? p.Name
                                : attr.PropertyName;
                        })
                        .Where(n => n != null)
                        .Cast<string>()
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(n => n, StringComparer.Ordinal)
                        .ToList();

                    lines.Add(type.Name + ": " + string.Join(", ", names));
                }
            }

            return string.Join("\n", lines);
        }
    }
}
