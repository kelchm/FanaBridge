namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// Resolves <see cref="PropertySpec"/>s to live values for the rule engine. This is the
    /// engine's only window onto game data, and it keeps Core adapter-agnostic: the plugin
    /// layer implements it against its data sources (typed telemetry for built-ins, name
    /// lookup for user-picked properties); tests implement it with a dictionary.
    ///
    /// A missing or null property returns false — the engine treats that as "condition not
    /// satisfied" (the rule stays armed), never as an error. Numeric-backed booleans follow
    /// the 0/1 convention common in telemetry ints: non-zero is true.
    /// </summary>
    public interface IPropertyReader
    {
        /// <summary>Reads a numeric value, or returns false when the property is missing/null.</summary>
        bool TryGetNumber(PropertySpec spec, out double value);

        /// <summary>Reads a boolean value (numbers: non-zero is true), or returns false when
        /// the property is missing/null.</summary>
        bool TryGetBool(PropertySpec spec, out bool value);
    }
}
