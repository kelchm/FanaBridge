namespace FanaBridge.Display.Rules
{
    /// <summary>
    /// Host-side read of the current ITM param value stream — what the telemetry
    /// mapper last computed per param. Condition evaluation uses this for
    /// <see cref="PropertyKind.ItmField"/> sources (including baked <c>self</c>).
    ///
    /// Pure plumbing until E8 wires a producer; nothing live consumes it yet.
    /// Missing / never-written params return false (condition stays unsatisfied).
    /// </summary>
    public interface IItmFieldValueReader
    {
        /// <summary>
        /// Reads the latest scalar for <paramref name="paramId"/>, or returns false
        /// when no value has been published this session / frame.
        /// </summary>
        bool TryGetNumber(ushort paramId, out double value);
    }

    /// <summary>
    /// Optional sink the mapper (or a test double) writes into so an
    /// <see cref="IItmFieldValueReader"/> can serve condition evaluation.
    /// </summary>
    public interface IItmFieldValueSink
    {
        /// <summary>Publishes the latest computed scalar for <paramref name="paramId"/>.</summary>
        void Publish(ushort paramId, double value);
    }

    /// <summary>
    /// In-memory param value buffer: implements both sink and reader. Default empty;
    /// not wired into the live path until E8.
    /// </summary>
    public sealed class ItmFieldValueBuffer : IItmFieldValueReader, IItmFieldValueSink
    {
        private readonly System.Collections.Generic.Dictionary<ushort, double> _values =
            new System.Collections.Generic.Dictionary<ushort, double>();

        public void Publish(ushort paramId, double value) => _values[paramId] = value;

        public bool TryGetNumber(ushort paramId, out double value)
            => _values.TryGetValue(paramId, out value);

        /// <summary>Clears every published value (e.g. cold session edge).</summary>
        public void Clear() => _values.Clear();
    }

    /// <summary>
    /// Composes a base <see cref="IPropertyReader"/> with an optional
    /// <see cref="IItmFieldValueReader"/> so <see cref="PropertyKind.ItmField"/>
    /// resolves host-side. When the field reader is null, ItmField reads fail
    /// (same as today's SimHubPropertySource default branch).
    /// </summary>
    public sealed class PropertyReaderWithItmFields : IPropertyReader
    {
        private readonly IPropertyReader _inner;
        private readonly IItmFieldValueReader _itmFields;

        public PropertyReaderWithItmFields(IPropertyReader inner, IItmFieldValueReader itmFields = null)
        {
            _inner = inner ?? throw new System.ArgumentNullException(nameof(inner));
            _itmFields = itmFields;
        }

        public bool TryGetNumber(PropertySpec spec, out double value)
        {
            value = 0;
            if (spec == null)
                return false;
            if (spec.Kind == PropertyKind.ItmField)
                return TryGetItmField(spec.Name, out value);
            return _inner.TryGetNumber(spec, out value);
        }

        public bool TryGetBool(PropertySpec spec, out bool value)
        {
            value = false;
            if (spec == null)
                return false;
            if (spec.Kind == PropertyKind.ItmField)
            {
                if (!TryGetItmField(spec.Name, out double n))
                    return false;
                value = n != 0 && !double.IsNaN(n);
                return true;
            }
            return _inner.TryGetBool(spec, out value);
        }

        private bool TryGetItmField(string name, out double value)
        {
            value = 0;
            if (_itmFields == null || string.IsNullOrEmpty(name))
                return false;
            // FromV2 bakes self → decimal/hex param id string.
            if (!TryParseParamId(name, out ushort paramId))
                return false;
            return _itmFields.TryGetNumber(paramId, out value);
        }

        /// <summary>Accepts decimal ("66") or 0x-prefixed hex ("0x42") param id spellings.</summary>
        public static bool TryParseParamId(string name, out ushort paramId)
        {
            paramId = 0;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            name = name.Trim();
            if (name.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("0X", System.StringComparison.OrdinalIgnoreCase))
            {
                return ushort.TryParse(
                    name.Substring(2),
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out paramId);
            }
            return ushort.TryParse(
                name,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out paramId);
        }
    }
}
