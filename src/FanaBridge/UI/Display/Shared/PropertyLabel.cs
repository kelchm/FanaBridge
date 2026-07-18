using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace FanaBridge.UI.Display.Shared
{
    /// <summary>Runs + full text bundled for <see cref="PropertyLabel.ContentSourceProperty"/>
    /// — the shape a data-bound (picker) row hands the label.</summary>
    public sealed class PropertyLabelContent
    {
        public PropertyLabelContent(IReadOnlyList<GrammarRun> runs, string fullText)
        {
            Runs = runs;
            FullText = fullText;
        }

        public IReadOnlyList<GrammarRun> Runs { get; }
        public string FullText { get; }
    }

    /// <summary>
    /// A thin TextBlock that renders a <see cref="PropertyGrammar"/> run list: dim namespace
    /// (<see cref="DisplayPalette.NsDim"/>), bright leaf (<see cref="DisplayPalette.LeafBright"/>),
    /// search-match highlight bold gold (<see cref="DisplayPalette.MatchHighlight"/>),
    /// monospace once there is anything to colour, with the full path as its tooltip and a
    /// character-ellipsis backstop. House style — code-built, no DataTemplate; the decision of
    /// WHAT to show lives in the pure grammar.
    /// </summary>
    public class PropertyLabel : TextBlock
    {
        public PropertyLabel()
        {
            TextTrimming = TextTrimming.CharacterEllipsis;
            VerticalAlignment = VerticalAlignment.Center;
        }

        /// <summary>Fill the label from a run list; <paramref name="fullText"/> becomes the
        /// tooltip. A monospace face is applied only when a Dim/Bright/Highlight run is present,
        /// so a Plain-only label (the placeholder, or a picker group header) keeps its
        /// inherited font.</summary>
        public void SetRuns(IReadOnlyList<GrammarRun> runs, string fullText)
        {
            Inlines.Clear();
            bool mono = false;
            if (runs != null)
            {
                foreach (var run in runs)
                {
                    var r = new Run(run.Text);
                    switch (run.Emphasis)
                    {
                        case GrammarEmphasis.Dim:
                            r.Foreground = DisplayPalette.NsDim;
                            mono = true;
                            break;
                        case GrammarEmphasis.Bright:
                            r.Foreground = DisplayPalette.LeafBright;
                            mono = true;
                            break;
                        case GrammarEmphasis.Highlight:
                            r.Foreground = DisplayPalette.MatchHighlight;
                            r.FontWeight = FontWeights.Bold;
                            mono = true;
                            break;
                        // Plain: no explicit foreground — inherit.
                    }
                    Inlines.Add(r);
                }
            }

            if (mono)
            {
                FontFamily = DisplayPalette.Mono;
                FontSize = 11;
            }
            else
            {
                // Recycled containers may carry a prior mono face — reset to inherited.
                ClearValue(FontFamilyProperty);
                ClearValue(FontSizeProperty);
            }

            ToolTip = string.IsNullOrEmpty(fullText) ? null : fullText;
        }

        /// <summary>A ready-made label for one property name.</summary>
        public static PropertyLabel ForProperty(string propertyName, PropertyDisplayKind kind, int charBudget)
        {
            var label = new PropertyLabel();
            label.SetRuns(
                PropertyGrammar.Format(propertyName, kind, charBudget),
                PropertyGrammar.FullText(propertyName, kind));
            return label;
        }

        // ── Attached ContentSource (data-bound rows, e.g. the property picker) ──

        /// <summary>Bind a <see cref="PropertyLabelContent"/> here to fill a PropertyLabel in a
        /// DataTemplate — the one place a template is unavoidable (a virtualized list).</summary>
        public static readonly DependencyProperty ContentSourceProperty =
            DependencyProperty.RegisterAttached(
                "ContentSource", typeof(PropertyLabelContent), typeof(PropertyLabel),
                new PropertyMetadata(null, OnContentSourceChanged));

        public static void SetContentSource(DependencyObject element, PropertyLabelContent value)
            => element.SetValue(ContentSourceProperty, value);

        public static PropertyLabelContent GetContentSource(DependencyObject element)
            => (PropertyLabelContent)element.GetValue(ContentSourceProperty);

        private static void OnContentSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is PropertyLabel label)
            {
                var content = e.NewValue as PropertyLabelContent;
                label.SetRuns(content?.Runs, content?.FullText);
            }
        }
    }
}
