namespace FanaBridge.Protocol.Schema
{
    /// <summary>
    /// One field in a fixed-layout HID report payload: its name, payload-relative
    /// byte offset, signedness, and the human metadata the docs show. A report's
    /// <c>ReportField[]</c> is the single source of truth — encode, decode, the doc
    /// table, and the golden tests all read from it.
    /// </summary>
    public sealed class ReportField
    {
        public string Name { get; }
        public int Offset { get; }
        public bool Signed { get; }
        public string Range { get; }
        public string Description { get; }

        public ReportField(string name, int offset, bool signed = false,
                           string range = "0–255", string description = "")
        {
            Name = name;
            Offset = offset;
            Signed = signed;
            Range = range;
            Description = description;
        }

        /// <summary>Wire type as the docs name it.</summary>
        public string TypeName => Signed ? "sbyte" : "byte";
    }
}
