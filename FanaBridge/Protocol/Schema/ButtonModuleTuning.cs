namespace FanaBridge.Protocol.Schema
{
    /// <summary>
    /// The button module (PBME/PBMR) exposes a tuning payload over the SAME col03
    /// 0x03 command as the wheelbase, but with its OWN layout — distinct from the
    /// wheelbase <see cref="TuningPayload"/>. Only the encoder-mode field is
    /// currently understood. The read→write <c>+1</c> shift is shared with the
    /// wheelbase payload.
    ///
    /// This dissolves the former bare <c>READ/WRITE_ENCODER_MODE_OFFSET = 18/19</c>
    /// magic numbers into a single fact: encoder mode at payload offset 15,
    /// expressed in read vs write HID coordinates.
    /// </summary>
    public static class ButtonModuleTuning
    {
        public const int ReadDataStart = TuningPayload.ReadDataStart;   // 3
        public const int WriteDataStart = TuningPayload.WriteDataStart; // 4

        /// <summary>Encoder operating mode — payload offset 15 on the button module.</summary>
        public static readonly ReportField EncoderMode =
            new ReportField("EncoderMode", 15, range: "0–3", description: "Button-module encoder operating mode");

        /// <summary>Encoder-mode index in a raw READ frame (HID byte 18).</summary>
        public static int ReadOffset => ReadDataStart + EncoderMode.Offset;

        /// <summary>Encoder-mode index in a raw WRITE frame (HID byte 19).</summary>
        public static int WriteOffset => WriteDataStart + EncoderMode.Offset;
    }
}
