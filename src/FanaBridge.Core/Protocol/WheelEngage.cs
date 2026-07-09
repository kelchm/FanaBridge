using FanaBridge.Transport;

namespace FanaBridge.Protocol
{
    /// <summary>
    /// Replicates the kernel filter's engage sequence (FWFUProtocolUsbBulkOrInterruptTransfer)
    /// for a Fanatec-class device: enable + trigger the col03 <c>FF 08</c> system report, then the
    /// col01 SubId triggers — SubId=1 (module-detection refresh), SubId=0 (input-report trigger),
    /// and SubId=4 (undocumented, sent for parity). This is what makes a device — a genuine base
    /// OR an SRM converter — volunteer its identity on whichever surface it uses.
    ///
    /// FanaBridge 0.4.0 sent only the <c>FF 08</c> pair on col03, once — a fraction of the filter's
    /// handshake — which is why a converter (that needs the fuller engage to emit its rim/module)
    /// came back empty. All sends here are usermode-legal HID output reports; the col01 SubId frames
    /// are the same 8-byte shape FanaBridge already writes for LEDs.
    ///
    /// Read/identify-only — none of these commands write tuning/config to the device.
    /// </summary>
    internal sealed class WheelEngage
    {
        private const int Col03Length = 64;

        // col03 FF 08 system-report enable + one-shot trigger.
        private static byte[] Ff08Enable()  { var b = new byte[Col03Length]; b[0] = 0xFF; b[1] = 0x08; b[2] = 0x01; b[3] = 0xFF; return b; }
        private static byte[] Ff08Trigger() { var b = new byte[Col03Length]; b[0] = 0xFF; b[1] = 0x08; b[2] = 0x02; return b; }

        // col01 SubId trigger: [report-id 01] F8 09 01 06 FF &lt;SubId&gt; 00.
        internal static byte[] SubId(byte subId) => new byte[8] { 0x01, 0xF8, 0x09, 0x01, 0x06, 0xFF, subId, 0x00 };

        /// <summary>One engage send and whether the transport accepted it.</summary>
        public struct Step
        {
            public string Label;
            public bool Sent;
            public Step(string label, bool sent) { Label = label; Sent = sent; }
        }

        /// <summary>
        /// Sends the full engage in the filter's order — <c>FF 08</c> enable, <c>FF 08</c> trigger
        /// (col03), then SubId=1 (module refresh), SubId=0 (input trigger), SubId=4 (col01). Held
        /// under one batch so no other write interleaves the sequence. Returns each send and whether
        /// the transport accepted it, so callers can prove the SubId=1 module refresh actually went
        /// out (vs. a closed col01 handle silently dropping it).
        /// </summary>
        public Step[] Engage(IDeviceTransport io)
        {
            using (io.BeginBatch())
            {
                return new[]
                {
                    new Step("FF08 enable",   io.SendCol03(Ff08Enable())),
                    new Step("FF08 trigger",  io.SendCol03(Ff08Trigger())),
                    new Step("SubId=1 module", io.SendCol01(SubId(0x01))), // -> col01 type-1 module record
                    new Step("SubId=0 input",  io.SendCol01(SubId(0x00))), // -> col01 identity report
                    new Step("SubId=4",        io.SendCol01(SubId(0x04))), // parity with the filter
                };
            }
        }
    }
}
