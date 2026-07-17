using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace FanaBridge.Adapters
{
    /// <summary>
    /// The reflection primitives both Control Mapper reflectors share — the live
    /// <see cref="ControlMapperBridge"/> (variant-provider registration + diagnostics)
    /// and the read-only <see cref="ControlMapperRoleReader"/> (mapped-role surfacing).
    /// Control Mapper exposes no public API for either job, so both reach the plugin and
    /// its settings by name; this class holds the ONE copy of that name-reaching so the
    /// two callers can't drift apart.
    ///
    /// Deliberately stateless and policy-free: pure primitives with no logging, no
    /// caching, no give-up threshold, no locking. Each caller keeps its own resolve
    /// wrapper (the bridge caches a closed generic and enforces a persistent-failure
    /// give-up; the reader re-resolves every call and degrades to its catalog) so this
    /// shared layer never imposes one caller's lifetime policy on the other. Read-only:
    /// nothing here writes to Control Mapper.
    /// </summary>
    internal static class ControlMapperReflection
    {
        /// <summary>The Control Mapper plugin's type name, resolved against the SimHub
        /// assembly the PluginManager lives in.</summary>
        public const string PluginTypeName =
            "SimHub.Plugins.OutputPlugins.ControlRemapper.ControlMapperPlugin";

        /// <summary>The ControlMapperPlugin type (from the PluginManager's own assembly),
        /// or null when Control Mapper isn't present.</summary>
        public static Type FindPluginType(object pm)
            => pm?.GetType().Assembly.GetType(PluginTypeName, throwOnError: false);

        /// <summary>The open generic definition of <c>PluginManager.GetPlugin&lt;T&gt;()</c>
        /// (parameterless, one type arg), or null when the shape isn't found.</summary>
        public static MethodInfo FindGetPluginMethod(Type pmType)
            => pmType?
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetPlugin"
                                  && m.IsGenericMethodDefinition
                                  && m.GetParameters().Length == 0
                                  && m.GetGenericArguments().Length == 1);

        /// <summary>ControlMapperPlugin.controlMapperPluginSettings (public field).</summary>
        public static object ReadSettings(object plugin)
            => plugin?.GetType()
                .GetField("controlMapperPluginSettings", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(plugin);

        /// <summary>The settings' <c>ControllerMappings</c> as an object sequence, or null
        /// when it isn't an enumerable (so callers can branch on absence exactly as the
        /// raw <c>is IEnumerable</c> test did).</summary>
        public static IEnumerable<object> ControllerMappingsOf(object settings)
            => GetProp(settings, "ControllerMappings") is IEnumerable maps ? maps.Cast<object>() : null;

        /// <summary>Read a public instance property by name, swallowing any reflection
        /// surprise (missing member, wrong type, disposed instance) as null.</summary>
        public static object GetProp(object o, string name)
        {
            try { return o?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o); }
            catch { return null; }
        }
    }
}
