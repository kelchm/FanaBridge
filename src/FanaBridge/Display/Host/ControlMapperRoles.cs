using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FanaBridge.Adapters;
using static FanaBridge.Adapters.ControlMapperReflection;

namespace FanaBridge.Display.Host
{
    /// <summary>Where <see cref="MappedRoles.Roles"/> came from — lets the mapped-control
    /// add flow hint the user whether the list is what is actually bound on this wheel or
    /// the full role catalog.</summary>
    internal enum MappedRolesSource
    {
        /// <summary>No roles at all (Control Mapper unavailable and no catalog).</summary>
        None,
        /// <summary>The distinct roles bound to this rim's buttons in Control Mapper.</summary>
        MappedOnThisWheel,
        /// <summary>The roles bound across MORE THAN ONE Fanatec base, unioned because
        /// "Recognize Individual Wheels" is off and no interface path was available to tell
        /// the active base apart. The roles are real, but they cannot be claimed as the ones
        /// mapped on THIS wheel specifically — an honest aggregate. (Full interface-path
        /// disambiguation lands in R2.)</summary>
        AggregatedAcrossBases,
        /// <summary>The sanctioned role catalog — this rim has no mappings of its own
        /// (or they were unreadable), so every assignable role is offered.</summary>
        AllRoles,
    }

    /// <summary>The mapped-control role list plus its provenance.</summary>
    internal sealed class MappedRoles
    {
        public static readonly MappedRoles None =
            new MappedRoles(Array.Empty<string>(), MappedRolesSource.None);

        public MappedRoles(IReadOnlyList<string> roles, MappedRolesSource source)
        {
            Roles = roles ?? Array.Empty<string>();
            Source = source;
        }

        public IReadOnlyList<string> Roles { get; }

        public MappedRolesSource Source { get; }
    }

    /// <summary>One Control Mapper button's role binding, extracted from the (unsanctioned)
    /// settings model so the pure resolver never touches SimHub types.</summary>
    internal readonly struct MappedButtonView
    {
        public MappedButtonView(string targetRole, bool hasRoleAssigned)
        {
            TargetRole = targetRole;
            HasRoleAssigned = hasRoleAssigned;
        }

        public string TargetRole { get; }
        public bool HasRoleAssigned { get; }
    }

    /// <summary>One Control Mapper source controller (its identity key plus its button
    /// bindings), extracted for the pure resolver.</summary>
    internal readonly struct ControllerMappingView
    {
        public ControllerMappingView(int vendorId, string variant, string interfacePath,
            IReadOnlyList<MappedButtonView> buttons)
        {
            VendorId = vendorId;
            Variant = variant;
            InterfacePath = interfacePath;
            Buttons = buttons ?? Array.Empty<MappedButtonView>();
        }

        public int VendorId { get; }
        public string Variant { get; }
        public string InterfacePath { get; }
        public IReadOnlyList<MappedButtonView> Buttons { get; }
    }

    /// <summary>
    /// Pure decision layer for <see cref="IMappedRoleCatalog.GetMappedRoles"/>: given the
    /// Control Mapper source mappings (as plain views), this rim's own identity key, and a
    /// sanctioned catalog fallback, it returns the roles to offer and where they came from.
    /// No SimHub types, no reflection — the reflection lives in
    /// <see cref="ControlMapperRoleReader"/>; this is what the tests pin.
    ///
    /// Match key mirrors Control Mapper's own precedence (InterfacePath+Variant strongest),
    /// narrowed to Fanatec controllers:
    /// <list type="bullet">
    /// <item><description>RIW on — this rim owns a variant: the Fanatec mappings whose
    ///   Variant equals ours (further narrowed by InterfacePath when both are known and
    ///   that narrowing leaves anything).</description></item>
    /// <item><description>RIW off — <paramref name="variant"/> is null: the Fanatec
    ///   mapping(s) with no variant (one physical base collapses to a single
    ///   InterfacePath-keyed row), optionally narrowed by InterfacePath.</description></item>
    /// </list>
    /// Any failure or an empty result falls through to the catalog, then to empty.
    /// </summary>
    internal static class MappedRoleResolver
    {
        /// <summary>Fanatec USB vendor id — the guard that keeps a foreign controller with
        /// a coincidentally-matching key out of the result.</summary>
        public const int FanatecVendorId = FanaBridgeVariantProvider.FanatecVendorId;

        public static MappedRoles Resolve(
            IReadOnlyList<ControllerMappingView> mappings,
            string variant,
            string interfacePath,
            Func<IReadOnlyList<string>> catalog)
        {
            var candidates = MatchingCandidates(mappings, variant, interfacePath);
            var mine = DistinctRoles(candidates);
            if (mine.Count > 0)
            {
                // RIW off with no interface path leaves us unable to tell which physical
                // base is active, so the candidates COULD span SEVERAL Fanatec bases and
                // `mine` would be their union. But the settings can also hold stale/duplicate
                // rows (two rows for the same controller, or a real row plus an empty-path
                // duplicate) — raw row count alone can't tell "several bases" from "one base,
                // several rows". Decide from distinct contributing controller identities
                // instead: how many DIFFERENT non-empty InterfacePath values appear among the
                // candidates that actually contributed a role. Two or more distinct paths is a
                // genuine multi-base union — surface that honestly. Exactly one distinct path
                // collapses back to a single base ONLY when every path-less contributor is a
                // stale duplicate of that base — i.e. carries no role the pathed base didn't
                // already have. A path-less contributor that adds a DISTINCT role is a second,
                // unattributable base, so keep the aggregate honest. Zero distinct paths (every
                // contributing candidate lacks a path) is the true attribution-impossible case —
                // an aggregate too. RIW on (a variant narrows to one rim) and the single-row
                // case are unambiguous regardless of path. (Full path disambiguation lands in R2.)
                bool ambiguous = false;
                if (string.IsNullOrEmpty(variant) && string.IsNullOrEmpty(interfacePath)
                    && candidates.Count > 1)
                {
                    ambiguous = ContributingBasesAreAmbiguous(candidates);
                }
                return new MappedRoles(mine,
                    ambiguous ? MappedRolesSource.AggregatedAcrossBases
                              : MappedRolesSource.MappedOnThisWheel);
            }

            IReadOnlyList<string> cat = null;
            try { cat = catalog?.Invoke(); }
            catch { cat = null; }
            var distinctCatalog = Distinct(cat);
            if (distinctCatalog.Count > 0)
                return new MappedRoles(distinctCatalog, MappedRolesSource.AllRoles);

            return MappedRoles.None;
        }

        // The Fanatec source mappings that match this rim's key (the variant when RIW is
        // on, else the no-variant base rows), narrowed to a known InterfacePath when that
        // leaves anything — so a rim we can't path-match still resolves by variant/RIW-off.
        private static List<ControllerMappingView> MatchingCandidates(
            IReadOnlyList<ControllerMappingView> mappings, string variant, string interfacePath)
        {
            var candidates = new List<ControllerMappingView>();
            if (mappings == null || mappings.Count == 0)
                return candidates;

            bool riwOn = !string.IsNullOrEmpty(variant);

            foreach (var m in mappings)
            {
                if (m.VendorId != FanatecVendorId)
                    continue;
                bool keyed = riwOn
                    ? Equals(m.Variant, variant)
                    : string.IsNullOrEmpty(m.Variant);
                if (keyed)
                    candidates.Add(m);
            }

            // When we also know our InterfacePath, narrow to it — but only if that leaves
            // something, so a rim we can't path-match still resolves by variant/RIW-off.
            if (!string.IsNullOrEmpty(interfacePath))
            {
                var narrowed = candidates.Where(m => Equals(m.InterfacePath, interfacePath)).ToList();
                if (narrowed.Count > 0)
                    candidates = narrowed;
            }

            return candidates;
        }

        // Distinct TargetRole across the matched mappings, first-seen order.
        private static IReadOnlyList<string> DistinctRoles(IReadOnlyList<ControllerMappingView> candidates)
        {
            var roles = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in candidates)
                foreach (var b in m.Buttons)
                    if (b.HasRoleAssigned && !string.IsNullOrEmpty(b.TargetRole) && seen.Add(b.TargetRole))
                        roles.Add(b.TargetRole);
            return roles;
        }

        // Whether the union of assigned roles cannot honestly be attributed to a single
        // physical base. We look at the DIFFERENT non-empty InterfacePath values among the
        // candidates that actually contributed a role, plus the roles carried by path-less
        // contributors (a genuine base whose path was simply unreadable yields a null path):
        //   • two or more distinct paths → a genuine multi-base union → ambiguous;
        //   • exactly one distinct path  → one base, UNLESS a path-less contributor adds a
        //     role that pathed base lacks (a second, unattributable base) → ambiguous then;
        //   • zero distinct paths        → nothing is attributable → ambiguous.
        // A path-less contributor whose roles the single pathed base already carries is a
        // stale/duplicate row and does not force ambiguity.
        private static bool ContributingBasesAreAmbiguous(IReadOnlyList<ControllerMappingView> candidates)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pathedRoles = new HashSet<string>(StringComparer.Ordinal);
            var pathlessRoles = new List<string>();
            foreach (var m in candidates)
            {
                bool pathless = string.IsNullOrEmpty(m.InterfacePath);
                foreach (var b in m.Buttons)
                {
                    if (!b.HasRoleAssigned || string.IsNullOrEmpty(b.TargetRole))
                        continue;
                    if (pathless)
                        pathlessRoles.Add(b.TargetRole);
                    else
                    {
                        paths.Add(m.InterfacePath);
                        pathedRoles.Add(b.TargetRole);
                    }
                }
            }

            // A genuine multi-base union across distinct pathed controllers.
            if (paths.Count >= 2)
                return true;

            // With no pathed base (paths.Count == 0) every contributed role is unattributable;
            // with exactly one, a path-less role that base doesn't carry is a second base.
            foreach (var role in pathlessRoles)
                if (!pathedRoles.Contains(role))
                    return true;

            return false;
        }

        private static IReadOnlyList<string> Distinct(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
                return Array.Empty<string>();
            var roles = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in values)
                if (!string.IsNullOrEmpty(v) && seen.Add(v))
                    roles.Add(v);
            return roles;
        }

        private static bool Equals(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reflects Control Mapper's live state into the plain views
    /// <see cref="MappedRoleResolver"/> consumes, following the same defensive posture as
    /// <see cref="ControlMapperBridge"/>: the per-controller button→role table is an
    /// unsanctioned public field (<c>controlMapperPluginSettings</c>) reached by name, and
    /// any shape surprise (missing member, wrong type, disposed instance) is swallowed and
    /// degrades to the sanctioned catalog rather than throwing at the caller. The role
    /// catalog itself comes from the sanctioned API
    /// (<c>GetControlMapperInterface().GetAvailableButtonRoles()</c>), also invoked by
    /// name so the reader stays testable with a stand-in plugin manager. Read-only: never
    /// writes to Control Mapper.
    /// </summary>
    internal sealed class ControlMapperRoleReader
    {
        /// <summary>Resolve the roles to offer for the rim whose FanaBridge variant is
        /// <paramref name="computedVariant"/> (and, when known, DirectInput
        /// <paramref name="interfacePath"/>). RIW-off is detected from the live settings,
        /// which nulls the variant so the InterfacePath / single-base row is matched
        /// instead.</summary>
        public MappedRoles Read(object pm, string computedVariant, string interfacePath)
        {
            if (pm == null)
                return MappedRoles.None;

            bool riwOn;
            var mappings = TryReadMappings(pm, out riwOn);
            string variant = riwOn ? computedVariant : null;
            return MappedRoleResolver.Resolve(mappings, variant, interfacePath,
                () => TryReadCatalog(pm));
        }

        // The unsanctioned path: GetPlugin<ControlMapperPlugin>().controlMapperPluginSettings
        // → RecognizeIndiviualWheels + ControllerMappings[].(ControllerDescription,
        // ControllerMapping.Buttons). Returns null on any failure (→ catalog fallback);
        // riwOn defaults to true so a readable-but-RIW-unknown config still trusts the variant.
        private IReadOnlyList<ControllerMappingView> TryReadMappings(object pm, out bool riwOn)
        {
            riwOn = true;
            try
            {
                object cm = LiveControlMapper(pm);
                if (cm == null)
                    return null;
                object settings = ReadSettings(cm);
                if (settings == null)
                    return null;

                if (GetProp(settings, "RecognizeIndiviualWheels") is bool riw)
                    riwOn = riw;

                var maps = ControllerMappingsOf(settings);
                if (maps == null)
                    return null;

                var views = new List<ControllerMappingView>();
                foreach (object csm in maps)
                {
                    if (csm == null)
                        continue;
                    object desc = GetProp(csm, "ControllerDescription");
                    int vendorId = desc != null && GetProp(desc, "VendorID") is int v ? v : 0;
                    string variant = desc != null ? GetProp(desc, "Variant") as string : null;
                    string ifacePath = desc != null ? GetProp(desc, "InterfacePath") as string : null;

                    var buttons = new List<MappedButtonView>();
                    object mapping = GetProp(csm, "ControllerMapping");
                    if (mapping != null && GetProp(mapping, "Buttons") is IEnumerable btns)
                        foreach (object b in btns)
                        {
                            if (b == null)
                                continue;
                            string role = GetProp(b, "TargetRole") as string;
                            bool has = GetProp(b, "HasRoleAssigned") is bool ha && ha;
                            buttons.Add(new MappedButtonView(role, has));
                        }

                    views.Add(new ControllerMappingView(vendorId, variant, ifacePath, buttons));
                }
                return views;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug(
                    "FanaBridge: Control Mapper mapping read failed: " + ex.GetBaseException().Message);
                return null;
            }
        }

        // The sanctioned path: PluginManager.GetControlMapperInterface().GetAvailableButtonRoles().
        private IReadOnlyList<string> TryReadCatalog(object pm)
        {
            try
            {
                MethodInfo getIface = pm.GetType().GetMethod("GetControlMapperInterface",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                object cmi = getIface?.Invoke(pm, null);
                if (cmi == null)
                    return null;
                MethodInfo getRoles = cmi.GetType().GetMethod("GetAvailableButtonRoles",
                    BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (!(getRoles?.Invoke(cmi, null) is IEnumerable roles))
                    return null;
                var list = new List<string>();
                foreach (object r in roles)
                    if (r is string s)
                        list.Add(s);
                return list;
            }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug(
                    "FanaBridge: Control Mapper role catalog read failed: " + ex.GetBaseException().Message);
                return null;
            }
        }

        // Uncached by design: re-resolves the live plugin every call and silently returns
        // null on a miss, so a transiently-unloaded Control Mapper degrades to the catalog
        // rather than sticking off. (The bridge, which mutates provider state, keeps its own
        // cached resolve + give-up policy instead.)
        private object LiveControlMapper(object pm)
        {
            Type cmType = FindPluginType(pm);
            if (cmType == null)
                return null;
            MethodInfo getPluginDef = FindGetPluginMethod(pm.GetType());
            if (getPluginDef == null)
                return null;
            return getPluginDef.MakeGenericMethod(cmType).Invoke(pm, null);
        }
    }
}
