using System;
using System.Collections.Generic;
using FanaBridge.Display.Rules;

namespace FanaBridge.Tests.Display.TestSupport
{
    /// <summary>
    /// Shared IPropertyReader fake for Display tests. Supports a constant value
    /// (FrameComposer-style) or a mutable name→number map (ItmField-style).
    /// </summary>
    public sealed class DictReader : IPropertyReader
    {
        private readonly double? _constant;

        public readonly Dictionary<string, double> Numbers =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        /// <summary>When set, only properties of this kind resolve — mirrors the
        /// pre-unification BuiltIn-only fake so kind-routing regressions still fail.</summary>
        public PropertyKind? RequireKind;

        public DictReader()
        {
        }

        public DictReader(double value) => _constant = value;

        public DictReader(Dictionary<string, double> nums)
        {
            if (nums == null)
                return;
            foreach (var kv in nums)
                Numbers[kv.Key] = kv.Value;
        }

        public bool TryGetNumber(PropertySpec spec, out double value)
        {
            if (_constant.HasValue)
            {
                value = _constant.Value;
                return true;
            }

            value = 0;
            if (spec == null)
                return false;
            if (RequireKind.HasValue && spec.Kind != RequireKind.Value)
                return false;
            if (Numbers.TryGetValue(spec.Name ?? "", out value))
                return true;
            return false;
        }

        public bool TryGetBool(PropertySpec spec, out bool value)
        {
            value = false;
            if (!TryGetNumber(spec, out double n))
                return false;
            value = n != 0;
            return true;
        }
    }
}
