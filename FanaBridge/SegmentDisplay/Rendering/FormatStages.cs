using System;

namespace FanaBridge.SegmentDisplay.Rendering
{
    /// <summary>Formats a raw value as a rounded integer string.</summary>
    public class NumberFormatter : IRenderStage
    {
        public SegmentDisplayFrame Process(SegmentDisplayFrame input, RenderContext ctx)
        {
            input.Text = FormatNumeric(input.Text, "0");
            return input;
        }

        internal static string FormatNumeric(string text, string fmt)
        {
            if (string.IsNullOrEmpty(text)) return "";
            try
            {
                if (double.TryParse(text, out double d)) return d.ToString(fmt);
                return text;
            }
            catch { return text; }
        }
    }

    /// <summary>Formats a raw value with one decimal place.</summary>
    public class DecimalFormatter : IRenderStage
    {
        public SegmentDisplayFrame Process(SegmentDisplayFrame input, RenderContext ctx)
        {
            input.Text = NumberFormatter.FormatNumeric(input.Text, "0.0");
            return input;
        }
    }

    /// <summary>Formats a TimeSpan or numeric value as a time string.</summary>
    public class TimeFormatter : IRenderStage
    {
        private readonly string _format;

        public TimeFormatter(string format = null)
        {
            _format = format ?? @"ss\.f";
        }

        public SegmentDisplayFrame Process(SegmentDisplayFrame input, RenderContext ctx)
        {
            if (string.IsNullOrEmpty(input.Text)) return input;

            // Try parsing as TimeSpan first (SimHub often provides TimeSpan.ToString())
            if (TimeSpan.TryParse(input.Text, out TimeSpan ts))
            {
                try { input.Text = ts.ToString(_format); }
                catch { input.Text = ts.ToString(@"ss\.f"); }
                return input;
            }

            // Fall back to numeric formatting
            input.Text = NumberFormatter.FormatNumeric(input.Text, "0.0");
            return input;
        }
    }

    /// <summary>Formats a gear value: R, N, 1-9.</summary>
    public class GearFormatter : IRenderStage
    {
        public SegmentDisplayFrame Process(SegmentDisplayFrame input, RenderContext ctx)
        {
            input.Text = FormatGear(input.Text);
            return input;
        }

        internal static string FormatGear(string text)
        {
            string g = (text ?? "").Trim().ToUpperInvariant();
            if (g == "R" || g == "REVERSE" || g == "-1") return "R";
            if (g == "N" || g == "NEUTRAL" || g == "0") return "N";
            int r;
            if (int.TryParse(g, out r)) return r.ToString();
            return g.Length > 0 ? g.Substring(0, Math.Min(g.Length, 3)) : "N";
        }
    }

    /// <summary>Passes the raw value through as-is.</summary>
    public class TextPassthrough : IRenderStage
    {
        public SegmentDisplayFrame Process(SegmentDisplayFrame input, RenderContext ctx)
        {
            if (input.Text == null) input.Text = "";
            return input;
        }
    }
}
