namespace FanaBridge.Devices
{
    /// <summary>
    /// A logical peripheral in the merged device view: what a SimHub adapter binds to
    /// by (kind, code), independent of which device/transport surfaces it. Today the
    /// set is { Base, Wheel|Hub, Module } from the one wheelbase; pedals/shifter join
    /// it (standalone or base-hosted) as more drivers land.
    /// </summary>
    public sealed class Peripheral
    {
        public PeripheralKind Kind { get; }

        /// <summary>FanaBridge code (e.g. "PSWBMW", "PHUB", "PBMR"), or null if unrecognized.</summary>
        public string Code { get; }

        /// <summary>Deepest firmware-defined wire byte this peripheral was decoded from.</summary>
        public byte WireCode { get; }

        /// <summary>Whether the hosting device's identity is settled (not mid-transition).</summary>
        public bool Stable { get; }

        public Peripheral(PeripheralKind kind, string code, byte wireCode, bool stable)
        {
            Kind = kind;
            Code = code;
            WireCode = wireCode;
            Stable = stable;
        }
    }
}
