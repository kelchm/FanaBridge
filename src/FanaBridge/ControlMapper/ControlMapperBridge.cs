using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using SimHub.Plugins.OutputPlugins.ControlRemapper.Variants;

namespace FanaBridge.ControlMapper
{
    /// <summary>
    /// Reflection plumbing that registers a <see cref="FanaBridgeVariantProvider"/>
    /// into SimHub's Control Mapper so per-rim button mappings stay separate on a
    /// Podium DD / newer base. SimHub exposes no public API to register a variant
    /// provider (only the bundled Fanatec and Simucube providers are wired up by
    /// <c>VariantHelper.Start()</c>), so the bridge walks
    /// <c>ControlMapperPlugin → remapperWorker → variantHelper → VariantProviders</c>
    /// by name and inserts our provider.
    ///
    /// FanaBridge is a <em>gap-filler</em>: ours is appended LAST, behind SimHub's
    /// built-in Fanatec provider. SimHub wins for every wheel it can already name;
    /// ours only answers where SimHub returns null (a base it can't identify, e.g.
    /// a Podium DD). So existing Control Mapper mappings are never disturbed —
    /// enabling the feature adds per-rim identity only where there was none, and
    /// disabling it removes our provider with no further effect.
    ///
    /// Design notes (verified against the decompiled SimHub.Plugins):
    /// <list type="bullet">
    /// <item><description>
    ///   <c>VariantHelper.GetVariant</c> resolves with
    ///   <c>FirstOrDefault(v =&gt; v != null)</c> over the list, so appending ours
    ///   LAST keeps the stock Fanatec provider ahead: it wins for any wheel it can
    ///   identify, and ours is consulted only where stock returns null. Existing
    ///   mappings stay byte-identical; disabling removes only our provider.
    /// </description></item>
    /// <item><description>
    ///   The provider list is created lazily by <c>VariantHelper.Start()</c> and
    ///   <c>nulled</c> by <c>VariantHelper.Stop()</c> — and <c>RemapperWorker</c>
    ///   calls one or the other every loop based on the user's "Recognize
    ///   Individual Wheels" setting. So we only insert when the list is already
    ///   non-null (the user has the setting on); we never force <c>Start()</c>,
    ///   which would fight the worker. If our provider is later evicted (toggle
    ///   off→on rebuilds the default list), the next <see cref="EnsureRegistered"/>
    ///   tick re-appends it. Registration is an idempotent
    ///   "ensure ours is present, last", not one-shot.
    /// </description></item>
    /// <item><description>
    ///   We register by swapping in a fresh list (copy + append) rather than
    ///   mutating in place: <c>GetVariant</c> enumerates the list lock-free from
    ///   the worker thread, so an in-place mutation could throw "collection
    ///   modified". A reference swap is atomic for readers. The swap is done
    ///   under the same monitor (<c>typeof(VariantHelper)</c>) that
    ///   <c>Start()</c>/<c>Stop()</c> use, so we never race those.
    /// </description></item>
    /// <item><description>
    ///   <c>VariantHelper</c> only subscribes to <c>VariantChanged</c> for
    ///   providers present when the list is first built, so it won't react to our
    ///   late-added provider's event. Instead we drive re-enumeration directly by
    ///   invoking <c>RemapperWorker.UpdateControllerList()</c> from FanaBridge's
    ///   own <c>WheelChanged</c> signal (<see cref="RequestReEnumerate"/>) — our
    ///   most reliable trigger.
    /// </description></item>
    /// </list>
    ///
    /// Every reflection step is defensive: a SimHub rename logs a single warning
    /// (<see cref="LogGiveUp"/>) and disables the integration for the session,
    /// leaving the rest of the plugin untouched.
    /// </summary>
    public class ControlMapperBridge
    {
        private const string ControlMapperPluginTypeName =
            "SimHub.Plugins.OutputPlugins.ControlRemapper.ControlMapperPlugin";

        private readonly object _sync = new object();
        private readonly FanaBridgeVariantProvider _provider = new FanaBridgeVariantProvider();

        // Reflection handles resolved once from types (instance-independent).
        private bool _resolved;
        private MethodInfo _getPluginCM;       // closed generic PluginManager.GetPlugin<ControlMapperPlugin>()
        private FieldInfo _rwField;            // ControlMapperPlugin.remapperWorker
        private FieldInfo _vhField;            // RemapperWorker.variantHelper
        private FieldInfo _provField;          // VariantHelper.VariantProviders
        private MethodInfo _updListMethod;     // RemapperWorker.UpdateControllerList()
        private Type _vhLockType;              // typeof(VariantHelper) — the Start()/Stop() monitor

        private object _pm;                    // cached PluginManager for re-resolving live instances
        private bool _registered;
        private bool _everRegistered;
        private bool _giveUpLogged;

        /// <summary>Whether our provider is currently believed to be in Control Mapper's list.</summary>
        public bool IsRegistered => _registered;

        /// <summary>
        /// True once a reflection step has failed and the bridge has stopped
        /// retrying. Callers can use this to avoid logging their own timeout.
        /// </summary>
        public bool IsGivenUp => _giveUpLogged;

        /// <summary>
        /// Ensure our variant provider is present in Control Mapper's provider
        /// list. Idempotent and cheap to call every (throttled) tick. Returns
        /// true when the provider is registered, false when Control Mapper isn't
        /// ready yet, the user's "Recognize Individual Wheels" toggle is off, or
        /// a reflection step has failed (see <see cref="IsGivenUp"/>).
        /// </summary>
        /// <param name="pm">SimHub's <c>PluginManager</c> (typed as object so the
        /// bridge can be unit-tested with a stand-in).</param>
        public bool EnsureRegistered(object pm)
        {
            if (pm == null) return false;
            object rwToReEnum = null; // set when a re-enumerate is needed; dispatched AFTER releasing _sync
            lock (_sync)
            {
                if (_giveUpLogged) return false;
                _pm = pm;
                if (!ResolveHandles(pm)) return false;

                object cm = LiveControlMapper();
                if (cm == null) return false;          // plugin not loaded yet — quiet retry
                // Value-access + mutation are wrapped: like every other reflection path
                // here (Unregister / StampFriendlyNames / DescribeResolution), an
                // unexpected SimHub shape change or an object disposed/rebuilt mid-call
                // should quietly give up and retry next tick, not surface as repeated
                // warnings from the per-tick DataUpdate caller.
                try
                {
                    object rw = _rwField.GetValue(cm);
                    if (rw == null) return false;
                    object vh = _vhField.GetValue(rw);
                    if (vh == null) return false;

                    bool changed;
                    lock (_vhLockType)
                    {
                        if (!(_provField.GetValue(vh) is IList current))
                        {
                            // List is null — "Recognize Individual Wheels" is off and
                            // the worker has Stop()'d. Nothing to register against; a
                            // variant wouldn't be consulted anyway. Reflect that.
                            _registered = false;
                            return false;
                        }

                        // FanaBridge is a gap-filler: ours goes LAST so SimHub's stock
                        // Fanatec provider stays ahead and wins for every wheel it can
                        // identify (Control Mapper takes the first non-null answer). Ours
                        // is consulted only where stock returns null — a base SimHub
                        // can't identify. Existing mappings are never disturbed.
                        int mineCount = 0;
                        foreach (var p in current)
                            if (p is FanaBridgeVariantProvider) mineCount++;
                        bool oursLast = current.Count > 0 && ReferenceEquals(current[current.Count - 1], _provider);

                        if (mineCount == 1 && oursLast)
                        {
                            _registered = true;
                            return true;                    // already present exactly once, last
                        }

                        // Rebuild: every non-FanaBridge provider in original order, then
                        // ours appended last (drops any stale FanaBridge provider left by
                        // a prior plugin reload).
                        var newList = new List<IVariantProvider>();
                        foreach (var p in current)
                            if (!(p is FanaBridgeVariantProvider))
                                newList.Add((IVariantProvider)p);
                        newList.Add(_provider);
                        _provField.SetValue(vh, newList);
                        changed = true;
                    }

                    _registered = true;
                    if (changed)
                    {
                        if (!_everRegistered)
                        {
                            _everRegistered = true;
                            SimHub.Logging.Current.Info(
                                "FanaBridge: Control Mapper integration active (variant provider registered)");
                        }
                        else
                        {
                            SimHub.Logging.Current.Info(
                                "FanaBridge: Control Mapper variant provider re-registered after eviction");
                        }
                        rwToReEnum = rw; // defer the re-enumerate to outside _sync (see ReEnumerateOutsideLock)
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Debug(
                        "FanaBridge: Control Mapper EnsureRegistered failed: " + ex.GetBaseException().Message);
                    return false;
                }
            }
            // A newly-registered provider needs the live controller list re-keyed —
            // dispatch OUTSIDE _sync (see ReEnumerateOutsideLock) to avoid the deadlock.
            ReEnumerateOutsideLock(rwToReEnum);
            return true;
        }

        /// <summary>
        /// Force Control Mapper to re-enumerate controllers so a just-swapped rim
        /// picks up its new variant immediately. Wired to FanaBridge's
        /// <c>WheelChanged</c>. No-op until registered.
        /// </summary>
        public void RequestReEnumerate()
        {
            object rw = null;
            lock (_sync)
            {
                if (!_registered || _updListMethod == null || _getPluginCM == null || _pm == null)
                    return;
                try
                {
                    object cm = LiveControlMapper();
                    rw = cm != null ? _rwField.GetValue(cm) : null;
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Debug(
                        "FanaBridge: Control Mapper re-enumerate lookup failed: " + ex.Message);
                    return;
                }
            }
            // Dispatch OUTSIDE _sync — see ReEnumerateOutsideLock.
            ReEnumerateOutsideLock(rw);
        }

        /// <summary>
        /// Remove our provider from Control Mapper's list so disabling the
        /// feature (or a plugin reload / shutdown) doesn't leave a dead provider
        /// behind. Safe to call when never registered.
        /// </summary>
        public void Unregister()
        {
            lock (_sync)
            {
                if (!_resolved || _pm == null) { _registered = false; return; }
                try
                {
                    object cm = LiveControlMapper();
                    object rw = cm != null ? _rwField.GetValue(cm) : null;
                    object vh = rw != null ? _vhField.GetValue(rw) : null;
                    if (vh != null && _vhLockType != null)
                    {
                        lock (_vhLockType)
                        {
                            if (_provField.GetValue(vh) is IList current
                                && current.Cast<object>().Any(p => p is FanaBridgeVariantProvider))
                            {
                                var newList = new List<IVariantProvider>();
                                foreach (var p in current)
                                    if (!(p is FanaBridgeVariantProvider))
                                        newList.Add((IVariantProvider)p);
                                _provField.SetValue(vh, newList);
                                SimHub.Logging.Current.Info(
                                    "FanaBridge: Control Mapper variant provider removed");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Debug(
                        "FanaBridge: Control Mapper unregister failed: " + ex.Message);
                }
                finally
                {
                    _registered = false;
                }
            }
        }

        /// <summary>
        /// Sets a friendly <c>CustomName</c> on the connected wheel's configured Control
        /// Mapper source(s) so the UI shows the SimHub device's name (the user's device
        /// rename if set, else its short name) while the match key stays the stable id.
        /// Applies when the name is empty OR still holds a name FanaBridge itself stamped
        /// (so a device rename propagates and older stamps migrate) — but never touches a
        /// name the user typed in Control Mapper. SimHub's per-tick <c>CopyFrom</c> doesn't
        /// touch <c>CustomName</c>, so it persists across re-enumeration and restarts.
        /// Idempotent and cheap to call each reconcile; no-op until registered.
        /// </summary>
        public void StampFriendlyNames()
        {
            lock (_sync)
            {
                if (!_registered || !_resolved || _pm == null) return;

                string id, friendly;
                try
                {
                    id = FanaBridgeVariantProvider.ComputeCurrentVariant();
                    // Prefer the SimHub device's own name — the user's rename if they set
                    // one, otherwise its short name — so the Control Mapper controller
                    // matches what's shown in the Devices view. Fall back to the computed
                    // short/friendly name if no device instance is connected.
                    friendly = FanatecPlugin.Instance?.GetConnectedWheelDisplayName();
                    if (string.IsNullOrEmpty(friendly))
                        friendly = FanaBridgeVariantProvider.ComputeFriendlyName();
                }
                catch { return; }
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(friendly)) return;

                try
                {
                    object cm = LiveControlMapper();
                    object settings = cm != null ? ReadSettings(cm) : null;
                    if (!(GetProp(settings, "ControllerMappings") is IEnumerable maps)) return;

                    foreach (object csm in maps)
                    {
                        object desc = csm != null ? GetProp(csm, "ControllerDescription") : null;
                        if (desc == null) continue;
                        if (AsInt(desc, "VendorID") != FanaBridgeVariantProvider.FanatecVendorId) continue;
                        // Only the connected wheel's mapping (variant == its id).
                        if (!string.Equals(AsString(desc, "Variant"), id, StringComparison.OrdinalIgnoreCase)) continue;

                        string current = AsString(desc, "CustomName");
                        // Set when unnamed, or refresh a name WE previously stamped (so a
                        // device rename propagates and old stamps migrate) — but never a
                        // name the user typed in Control Mapper.
                        bool oursToSet = string.IsNullOrEmpty(current)
                            || FanaBridgeVariantProvider.IsFanaBridgeDefaultName(current);
                        if (!oursToSet) continue;
                        if (string.Equals(current, friendly, StringComparison.Ordinal)) continue; // already correct
                        if (SetCustomName(desc, friendly))
                            SimHub.Logging.Current.Debug(
                                "FanaBridge: set Control Mapper display name \"" + friendly + "\" for " + id);
                    }
                }
                catch (Exception ex)
                {
                    SimHub.Logging.Current.Debug("FanaBridge: StampFriendlyNames failed: " + ex.Message);
                }
            }
        }

        private static bool SetCustomName(object desc, string name)
        {
            try
            {
                PropertyInfo p = desc.GetType().GetProperty("CustomName", BindingFlags.Public | BindingFlags.Instance);
                if (p == null || !p.CanWrite) return false;
                p.SetValue(desc, name);
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Builds a read-only, human-readable snapshot of how Control Mapper is
        /// resolving the Fanatec wheel's variant right now — for the "Copy Debug
        /// Info" report. Shows (a) what FanaBridge's provider would emit, (b) what
        /// each registered variant provider returns for the live wheel — so the
        /// stock Fanatec provider's null-vs-non-null answer is visible side by
        /// side with ours (this is what lets a single-rim DD+ owner tell whether
        /// stock already identifies the base or whether FanaBridge is filling the
        /// gap), and (c) the variant Control Mapper has actually committed to each
        /// Fanatec controller row. Never mutates anything. Safe to call when the
        /// feature is off (construct a throwaway bridge), when "Recognize
        /// Individual Wheels" is off, or when Control Mapper isn't loaded.
        /// </summary>
        public string DescribeResolution(object pm)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Control Mapper integration (diagnostic)");
            sb.AppendLine("  Mode: gap-filler — SimHub wins where it recognizes a wheel; FanaBridge fills the rest.");

            string ours = null, friendly = null;
            try { ours = FanaBridgeVariantProvider.ComputeCurrentVariant(); } catch { }
            try { friendly = FanaBridgeVariantProvider.ComputeFriendlyName(); } catch { }
            sb.AppendLine("  FanaBridge would emit (id): " + (ours ?? "(none — no wheel detected)"));
            sb.AppendLine("  Friendly name (-> CustomName): " + (friendly ?? "(none)"));
            sb.AppendLine("  Bridge state: registered=" + _registered + ", givenUp=" + _giveUpLogged);

            try
            {
                if (pm == null) { sb.AppendLine("  PluginManager unavailable."); return sb.ToString(); }
                lock (_sync)
                {
                    _pm = pm;
                    if (!ResolveHandles(pm))
                    {
                        sb.AppendLine("  Control Mapper reflection unavailable (SimHub internals not found).");
                        return sb.ToString();
                    }
                    object cm = LiveControlMapper();
                    if (cm == null) { sb.AppendLine("  Control Mapper plugin is not loaded/enabled."); return sb.ToString(); }
                    object rw = _rwField.GetValue(cm);
                    object vh = rw != null ? _vhField.GetValue(rw) : null;

                    int pid = ResolveFanatecPid(cm);
                    sb.AppendLine("  Live wheel PID: 0x" + pid.ToString("X4"));

                    // Snapshot the provider list under the same monitor Start()/Stop() use,
                    // then probe each OUTSIDE the lock (a stock probe can do native I/O).
                    IVariantProvider[] providers = null;
                    if (vh != null && _vhLockType != null)
                    {
                        lock (_vhLockType)
                        {
                            if (_provField.GetValue(vh) is IList list)
                                providers = list.Cast<object>().OfType<IVariantProvider>().ToArray();
                        }
                    }

                    if (providers == null)
                    {
                        sb.AppendLine("  Provider list is null — Control Mapper 'Recognize Individual Wheels' is OFF.");
                        sb.AppendLine("  (Turn it on; without it Control Mapper never asks any provider for a variant.)");
                    }
                    else
                    {
                        sb.AppendLine("  Variant providers in priority order (first non-null wins):");
                        string winner = null;
                        bool winnerIsOurs = false;
                        foreach (var p in providers)
                        {
                            string v = null, err = null;
                            try { v = p.GetVariant(FanaBridgeVariantProvider.FanatecVendorId, pid); }
                            catch (Exception ex) { err = ex.GetBaseException().Message; }
                            if (winner == null && v != null) { winner = v; winnerIsOurs = p is FanaBridgeVariantProvider; }
                            sb.AppendLine("    - " + p.GetType().Name + " => " + (err != null ? "ERROR: " + err : (v ?? "null")));
                        }
                        sb.AppendLine("  Resolved (winning) variant: " + (winner ?? "null"));
                        sb.AppendLine("  Verdict: " + Verdict(winner, winnerIsOurs));
                    }

                    sb.AppendLine("  Your configured Control Mapper sources (Fanatec):");
                    int n = AppendConfiguredSources(cm, sb);
                    if (n == 0) sb.AppendLine("    (none configured yet)");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  diagnostic error: " + ex.GetBaseException().Message);
            }
            return sb.ToString();
        }

        /// <summary>
        /// Whether Control Mapper's "Recognize Individual Wheels" is currently on — the
        /// setting that makes Control Mapper consult variant providers at all. When it's
        /// off the worker Stop()s the provider list and no variant is ever requested, so
        /// the integration is a silent no-op. Returns null when it can't be determined
        /// (Control Mapper not loaded, or SimHub internals unavailable). Read-only: never
        /// registers or mutates. Used by the settings UI to surface the dependency.
        /// </summary>
        public bool? IsRecognizeIndividualWheelsOn(object pm)
        {
            if (pm == null) return null;
            try
            {
                lock (_sync)
                {
                    if (_giveUpLogged) return null;
                    _pm = pm;
                    if (!ResolveHandles(pm)) return null;
                    object cm = LiveControlMapper();
                    if (cm == null) return null;

                    // Primary: read the actual setting. The property name mirrors SimHub's,
                    // including its spelling ("Indiviual").
                    if (GetProp(ReadSettings(cm), "RecognizeIndiviualWheels") is bool on)
                        return on;

                    // Fallback: the worker Stop()s the provider list when the setting is
                    // off, so a present list means on and a null list means off.
                    object rw = _rwField.GetValue(cm);
                    object vh = rw != null ? _vhField.GetValue(rw) : null;
                    if (vh != null && _vhLockType != null)
                        lock (_vhLockType)
                            return _provField.GetValue(vh) is IList;
                    return null;
                }
            }
            catch { return null; }
        }

        // ---- internals -----------------------------------------------------

        /// <summary>Resolve all reflection handles once, purely from types.</summary>
        private bool ResolveHandles(object pm)
        {
            if (_resolved) return true;
            try
            {
                Assembly asm = pm.GetType().Assembly;
                Type cmType = asm.GetType(ControlMapperPluginTypeName, throwOnError: false);
                if (cmType == null) { LogGiveUp("ControlMapperPlugin type not found in " + asm.GetName().Name); return false; }

                MethodInfo getPluginDef = pm.GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetPlugin"
                                      && m.IsGenericMethodDefinition
                                      && m.GetParameters().Length == 0
                                      && m.GetGenericArguments().Length == 1);
                if (getPluginDef == null) { LogGiveUp("PluginManager.GetPlugin<T>() not found"); return false; }

                FieldInfo rwField = cmType.GetField("remapperWorker", BindingFlags.NonPublic | BindingFlags.Instance);
                if (rwField == null) { LogGiveUp("ControlMapperPlugin.remapperWorker field not found"); return false; }

                Type rwType = rwField.FieldType;
                FieldInfo vhField = rwType.GetField("variantHelper", BindingFlags.NonPublic | BindingFlags.Instance);
                if (vhField == null) { LogGiveUp("RemapperWorker.variantHelper field not found"); return false; }

                Type vhType = vhField.FieldType;
                FieldInfo provField = vhType.GetField("VariantProviders", BindingFlags.NonPublic | BindingFlags.Instance);
                if (provField == null) { LogGiveUp("VariantHelper.VariantProviders field not found"); return false; }

                MethodInfo updList = rwType.GetMethod("UpdateControllerList",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (updList == null)
                    SimHub.Logging.Current.Warn(
                        "FanaBridge: RemapperWorker.UpdateControllerList not found — rim hot-swap won't "
                        + "auto-refresh Control Mapper (a manual rescan still works).");

                _getPluginCM = getPluginDef.MakeGenericMethod(cmType);
                _rwField = rwField;
                _vhField = vhField;
                _provField = provField;
                _updListMethod = updList;
                _vhLockType = vhType;
                _resolved = true;
                return true;
            }
            catch (Exception ex)
            {
                LogGiveUp("unexpected exception resolving handles: " + ex.GetBaseException().Message);
                return false;
            }
        }

        // A throwing GetPlugin<T> during startup usually means the plugin
        // collection is still in flux — a transient, not a shape change. Shape
        // changes (the LogGiveUp calls in ResolveHandles) fail deterministically
        // on every call, so a persistent-failure threshold still catches them
        // here without letting one bad tick at startup kill the integration for
        // the whole session. Internal so tests pin the exact threshold.
        internal const int LIVE_RESOLVE_FAILURES_BEFORE_GIVE_UP = 10;
        private int _liveResolveFailures;

        /// <summary>Resolve the live ControlMapperPlugin instance (null if not loaded).</summary>
        private object LiveControlMapper()
        {
            try
            {
                object cm = _getPluginCM.Invoke(_pm, null);
                _liveResolveFailures = 0;   // any non-throwing call proves the path works
                return cm;
            }
            catch (Exception ex)
            {
                if (++_liveResolveFailures >= LIVE_RESOLVE_FAILURES_BEFORE_GIVE_UP)
                    LogGiveUp("GetPlugin<ControlMapperPlugin> failing persistently ("
                        + _liveResolveFailures + " consecutive): " + ex.GetBaseException().Message);
                else
                    SimHub.Logging.Current.Debug(
                        "FanaBridge: GetPlugin<ControlMapperPlugin> threw (attempt "
                        + _liveResolveFailures + ", will retry): " + ex.GetBaseException().Message);
                return null;
            }
        }

        // Invoke SimHub's UpdateControllerList OUTSIDE _sync. UpdateControllerList does a
        // synchronous Dispatcher.Invoke to the UI thread, which re-enters this bridge
        // (e.g. IsRecognizeIndividualWheelsOn takes _sync); holding _sync across it
        // deadlocks the plugin thread against the UI thread (WatchDog "Abnormal Inactivity").
        private void ReEnumerateOutsideLock(object rw)
        {
            if (rw == null) return;
            InvokeUpdateControllerList(rw);
        }

        private void InvokeUpdateControllerList(object rw)
        {
            if (_updListMethod == null) return;
            try { _updListMethod.Invoke(rw, null); }
            catch (Exception ex)
            {
                SimHub.Logging.Current.Debug(
                    "FanaBridge: Control Mapper UpdateControllerList threw: " + ex.GetBaseException().Message);
            }
        }

        private void LogGiveUp(string reason)
        {
            if (_giveUpLogged) return;
            _giveUpLogged = true;
            SimHub.Logging.Current.Warn(
                "FanaBridge: Control Mapper integration disabled for this session — " + reason
                + ". The wheelbase still works in Control Mapper, just without per-rim variants.");
        }

        // ---- diagnostic reflection (read-only) -----------------------------

        /// <summary>Best-effort live wheel PID: prefer FanaBridge's own wheelbase,
        /// else the first committed Fanatec controller's PID, else 0.</summary>
        private int ResolveFanatecPid(object cm)
        {
            try
            {
                int fromBase = FanatecPlugin.Instance?.Wheelbase?.ConnectedProductId ?? 0;
                if (fromBase != 0) return fromBase;
            }
            catch { }

            try
            {
                object settings = ReadSettings(cm);
                foreach (object cd in EnumerateDescriptions(settings))
                    if (AsInt(cd, "VendorID") == FanaBridgeVariantProvider.FanatecVendorId)
                        return AsInt(cd, "ProductId");
            }
            catch { }
            return 0;
        }

        /// <summary>Plain-English summary of what FanaBridge is doing for the live wheel.</summary>
        private static string Verdict(string winner, bool winnerIsOurs)
        {
            if (string.IsNullOrEmpty(winner))
                return "no per-rim identity (Recognize Individual Wheels off, or no wheel detected).";
            if (winnerIsOurs)
                return "SimHub can't identify this wheel — FanaBridge is supplying the identity \""
                    + winner + "\".";
            return "SimHub already identifies this wheel (\"" + winner + "\"); FanaBridge is standing by.";
        }

        /// <summary>Lists the user's configured Fanatec source controllers (the ones they
        /// added in Control Mapper) with their identity scheme, whether anything is bound,
        /// and whether they're connected — so it's clear what's set up and which entries
        /// are stale/duplicate left from earlier. Returns the count.</summary>
        private int AppendConfiguredSources(object cm, StringBuilder sb)
        {
            int count = 0;
            try
            {
                object settings = ReadSettings(cm);
                if (!(GetProp(settings, "ControllerMappings") is IEnumerable maps))
                {
                    sb.AppendLine("    (unavailable)");
                    return 0;
                }
                foreach (object csm in maps)
                {
                    if (csm == null) continue;
                    object desc = GetProp(csm, "ControllerDescription");
                    if (desc == null || AsInt(desc, "VendorID") != FanaBridgeVariantProvider.FanatecVendorId) continue;
                    count++;
                    string variant = AsString(desc, "Variant");
                    sb.AppendLine(string.Format("    - \"{0}\"  variant={1}  scheme={2}  bound={3}  connected={4}",
                        AsString(desc, "ControllerName") ?? "?",
                        string.IsNullOrEmpty(variant) ? "(none)" : variant,
                        Scheme(variant), ReadBound(csm), ReadConnected(csm)));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("    (read error: " + ex.GetBaseException().Message + ")");
            }
            return count;
        }

        // Variants are now stable ids ("FS_WHEEL_SWTYPE_*"), shared by stock and
        // FanaBridge alike. A non-id entry is an OLD FanaBridge friendly-name mapping
        // from before this change — surfaced here ("legacy") so it can be cleaned up.
        private static string Scheme(string variant)
        {
            if (string.IsNullOrEmpty(variant)) return "none";
            if (variant.StartsWith("FS_WHEEL_SWTYPE_", StringComparison.OrdinalIgnoreCase)) return "id";
            return "legacy-name (cleanup)";
        }

        private static string ReadConnected(object csm)
        {
            object st = GetProp(csm, "ControllerState");
            if (st == null) return "?";
            if (GetProp(st, "IsConnected") is bool c) return c ? "yes" : "no";
            if (GetProp(st, "Available") is bool a) return a ? "yes" : "no";
            return "?";
        }

        private static string ReadBound(object csm)
        {
            object map = GetProp(csm, "ControllerMapping");
            if (map == null) return "?";
            if (HasAssigned(map, "Buttons") || HasAssigned(map, "Axis")
                || HasAssigned(map, "Keys") || HasAssigned(map, "VirtualButtons"))
                return "yes";
            return "no";
        }

        private static bool HasAssigned(object map, string prop)
        {
            if (!(GetProp(map, prop) is IEnumerable coll)) return false;
            foreach (var item in coll)
                if (item != null && GetProp(item, "HasRoleAssigned") is bool b && b) return true;
            return false;
        }

        /// <summary>ControlMapperPlugin.controlMapperPluginSettings (public field).</summary>
        private static object ReadSettings(object cm)
        {
            return cm?.GetType()
                .GetField("controlMapperPluginSettings", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(cm);
        }

        /// <summary>Yields every ControllerDescription reachable from the settings —
        /// both AvailableControllers (live) and ControllerMappings[].ControllerDescription
        /// (configured) — without referencing the SimHub model types at compile time.</summary>
        private static IEnumerable<object> EnumerateDescriptions(object settings)
        {
            if (settings == null) yield break;

            if (GetProp(settings, "AvailableControllers") is IEnumerable avail)
                foreach (var d in avail)
                    if (d != null) yield return d;

            if (GetProp(settings, "ControllerMappings") is IEnumerable maps)
                foreach (var m in maps)
                {
                    if (m == null) continue;
                    object d = GetProp(m, "ControllerDescription");
                    if (d != null) yield return d;
                }
        }

        private static object GetProp(object o, string name)
        {
            try { return o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o); }
            catch { return null; }
        }

        private static int AsInt(object o, string prop)
        {
            return GetProp(o, prop) is int i ? i : 0;
        }

        private static string AsString(object o, string prop)
        {
            return GetProp(o, prop) as string;
        }
    }
}
