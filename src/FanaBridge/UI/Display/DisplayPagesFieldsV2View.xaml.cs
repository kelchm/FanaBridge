using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Rules;
using FanaBridge.Display.Runtime;
using FanaBridge.Display.Schema2;
using FanaBridge.Display.Session;
using FanaBridge.Display.Twin;
using FanaBridge.Profiles;
using FanaBridge.Protocol;
using FanaBridge.UI.Display.Shared;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Pages &amp; Fields view (5c/5d/5g/5p + 8c field filter). Pure projection via
    /// <see cref="DisplayPagesFieldsV2Model"/>; every write opens a
    /// <see cref="DisplayConfigV2EditSession"/>, mutates, and <c>TryApply</c>s.
    /// §DIVERGENCES D1–D15 are sanctioned; each carries a comment at the build site.
    /// </summary>
    public partial class DisplayPagesFieldsV2View : UserControl
    {
        private static readonly Brush AccentBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0xA0, 0xDD)));
        private static readonly Brush SelectedPageBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x18, 0x7F, 0xAD)));
        private static readonly Brush OutlineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4C)));
        private static readonly Brush WinnerBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x2F, 0x3D)));
        private static readonly Brush CardBg = Freeze(new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2D)));
        private static readonly Brush MutedFg = Freeze(new SolidColorBrush(Color.FromRgb(0x7C, 0x7C, 0x7E)));
        private static readonly Brush BodyFg = Freeze(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)));
        private static readonly Brush WhiteFg = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));
        private static readonly Brush DashedBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4C)));

        private IDisplayPanelHost _host;
        private IDisplayPropertyCatalog _propertyCatalog;
        private IMappedRoleCatalog _roleCatalog;
        private IDisplayPickerStore _pickerStore;
        private WheelCatalog _catalog;
        private AliasTable _aliases;
        private DisplayPagesFieldsV2Model _model;

        // View state (never document).
        private string _selectedPageKey;
        private ushort? _focusedParamId;
        private string _focusClearAnnouncement;

        // 5g form state
        private ushort _ovParamId;
        private string _ovOverrideId;
        private bool _ovIsNew;
        private ValueSourceKind _ovSourceKind = ValueSourceKind.SimHubProperty;
        /// <summary>Exact bring-up DurationMs from hydrate (or default). Written unless dirty.</summary>
        private int _ovBringUpDurationMs = Lifetime.DefaultDurationMs;
        /// <summary>True only after the user edits the seconds field this open.</summary>
        private bool _ovBringUpDurationDirty;
        /// <summary>Presentation seconds (may round); not the store of record.</summary>
        private int _ovBringUpSeconds = 5;
        private TextBox _txtOvBringUpSeconds;
        private int _ovHomeRank = 1;
        private string _ovHomeRowId;
        /// <summary>Prior Enabled (not on the form) — preserved across open→save.</summary>
        private bool _ovEnabled = true;

        // 5p dialog working order (page keys) + dirty / tri-state tracking
        private List<string> _rotationWorkingOrder;
        /// <summary>True when pageOrder was null at dialog open (absent = compiled default).</summary>
        private bool _rotationWasAbsent;
        /// <summary>True when the user edited membership or order inside the dialog.</summary>
        private bool _rotationDirty;

        private bool _suppressEvents;

        /// <summary>‹ Overview breadcrumb.</summary>
        public event EventHandler BackRequested;

        /// <summary>Raised after a successful session apply.</summary>
        public event EventHandler ConfigApplied;

        /// <summary>Priority › spoke (A-N12).</summary>
        public event EventHandler PriorityRequested;

        /// <summary>+ Add a page → Surface B (A-N5). Destination may be inert this phase.</summary>
        public event EventHandler AddPageRequested;

        private readonly SevenSegmentFace _segmentPreviewFace = new SevenSegmentFace();

        public DisplayPagesFieldsV2View()
        {
            InitializeComponent();
            ApplyStaticCopy();
            hostSegmentPreview.Content = _segmentPreviewFace;
            displayMirror.IsInteractive = true;
            displayMirror.SlotClicked += paramId => SelectFieldCore(paramId);
            // Card layout derives from the pane width. Polls no longer re-measure
            // (structure-gated), so window resizes must re-lay out themselves.
            dockFieldRegion.SizeChanged += (s, e) =>
            {
                if (_model != null && e.WidthChanged)
                    RebuildFieldCollection(_model);
            };
        }

        internal void Bind(
            IDisplayPanelHost host,
            WheelCatalog catalog = null,
            AliasTable aliases = null,
            IDisplayPropertyCatalog propertyCatalog = null,
            IMappedRoleCatalog roleCatalog = null,
            IDisplayPickerStore pickerStore = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _catalog = catalog;
            _aliases = aliases;
            _propertyCatalog = propertyCatalog;
            _roleCatalog = roleCatalog;
            _pickerStore = pickerStore;
            Poll(force: true);
        }

        internal void Poll(bool force = false)
        {
            if (_host == null)
                return;

            // A non-forced repaint must never yank the control the user is typing
            // in (the rebuild would also commit half-typed text via LostFocus).
            // Forced polls come from the user's own committed edit, so they pass.
            if (!force
                && (popupOverride.IsOpen
                    || popupRotation.IsOpen
                    || InlineEditGuard.IsEditingWithin(this)))
            {
                return;
            }

            var envelope = _host.Snapshot;
            var live = _host.GetDisplayConfigV2();
            var resolution = ProjectResolution(envelope);
            var values = envelope?.Values;
            var displayType = _host.DisplayType;

            _model = DisplayPagesFieldsV2Model.Project(
                live,
                resolution,
                values,
                displayType,
                _catalog,
                _aliases,
                _selectedPageKey,
                _focusedParamId);

            // Absorb model-resolved selection / focus (page-switch survival / clear).
            _selectedPageKey = _model.SelectedPageKey;
            if (_focusedParamId.HasValue && !_model.FocusedParamId.HasValue
                && !string.IsNullOrEmpty(_model.FocusClearAnnouncement))
            {
                _focusClearAnnouncement = _model.FocusClearAnnouncement;
            }
            _focusedParamId = _model.FocusedParamId;

            ApplyModel(_model);
        }

        private static DisplayResolutionSnapshotModel ProjectResolution(DisplayPanelSnapshot envelope)
        {
            if (envelope == null)
            {
                return DisplayResolutionSnapshotModel.From(
                    null, inGame: false, isConnected: false, aggregates: null, manual: null);
            }

            return DisplayResolutionSnapshotModel.From(
                envelope.ComposedResolution,
                inGame: envelope.InGame,
                isConnected: true,
                aggregates: envelope.Aggregates,
                manual: envelope.Manual);
        }

        /// <summary>Test seam: current pure model.</summary>
        internal DisplayPagesFieldsV2Model Model => _model;

        /// <summary>Test seam: selected page key.</summary>
        internal string SelectedPageKeyForTest => _selectedPageKey;

        /// <summary>Test seam: focused param.</summary>
        internal ushort? FocusedParamIdForTest => _focusedParamId;

        // ── Selection / filter cores (extracted for tests) ───────────────

        /// <summary>
        /// Dual-channel focus: preview hit or section header. Re-click clears (route 2).
        /// </summary>
        internal void SelectFieldCore(ushort paramId)
        {
            _focusedParamId = DisplayPagesFieldsV2Model.ToggleFocus(_focusedParamId, paramId);
            _focusClearAnnouncement = null;
            Poll(force: true);
        }

        /// <summary>Clear routes 1 / 3 / 4: named action, empty chrome, Esc.</summary>
        internal void ClearFocusCore()
        {
            _focusedParamId = DisplayPagesFieldsV2Model.ClearFocus();
            _focusClearAnnouncement = null;
            Poll(force: true);
        }

        /// <summary>Page strip selection (view state only).</summary>
        internal void SelectPageCore(string pageKey)
        {
            if (string.IsNullOrEmpty(pageKey))
                return;
            _selectedPageKey = pageKey;
            // Focus survival handled inside Project (D10).
            Poll(force: true);
        }

        /// <summary>
        /// Production override save: BuildOverrideFromForm → Add/UpdateOverride → TryApply.
        /// Also applies ActsAsEntrypoint + SetBringUpLifetime when flagged.
        /// </summary>
        internal void OverrideSaveCore()
        {
            var ov = BuildOverrideFromForm();
            bool acts = chkOvEntrypoint.IsChecked == true;
            string homeRowId = _ovHomeRowId;
            int bringUpMs = ResolveBringUpDurationMs();
            bool bringUpVisit = radBringUpVisit.IsChecked == true;
            ushort paramId = _ovParamId;
            string overrideId = _ovOverrideId;
            bool isNew = _ovIsNew;
            var catalog = _catalog;

            ApplyEdit(session =>
            {
                if (isNew)
                    session.AddOverride(paramId, ov, catalog);
                else
                    session.UpdateOverride(paramId, overrideId, ov, catalog);

                // ActsAsEntrypoint is also on the override; Update/Add carries it.
                // Bring-up lifetime lives on the home seat (not the override).
                if (acts && !string.IsNullOrEmpty(homeRowId))
                {
                    var life = new Lifetime
                    {
                        Kind = bringUpVisit ? LifetimeKind.ForDuration : LifetimeKind.WhileTrue,
                    };
                    if (bringUpVisit)
                        life.DurationMs = bringUpMs;
                    session.SetBringUpLifetime(homeRowId, life);
                }
                return session.Document;
            });
        }

        /// <summary>
        /// Stored bring-up ms: exact hydrated value unless the user edited seconds this open.
        /// </summary>
        private int ResolveBringUpDurationMs()
        {
            if (_ovBringUpDurationDirty)
                return Math.Max(1, _ovBringUpSeconds) * 1000;
            return _ovBringUpDurationMs > 0
                ? _ovBringUpDurationMs
                : Lifetime.DefaultDurationMs;
        }

        /// <summary>Presentation seconds from raw ms (round half-up; min 1).</summary>
        private static int PresentationSeconds(int durationMs)
            => Math.Max(1, (durationMs + 500) / 1000);

        /// <summary>Production override delete path.</summary>
        internal void OverrideDeleteCore()
        {
            if (_ovIsNew || string.IsNullOrEmpty(_ovOverrideId))
                return;
            ushort paramId = _ovParamId;
            string id = _ovOverrideId;
            var catalog = _catalog;
            ApplyEdit(session => session.RemoveOverride(paramId, id, catalog));
        }

        /// <summary>Production base-block write.</summary>
        internal void SetFieldBaseCore(ushort paramId, FieldBase bas)
        {
            var catalog = _catalog;
            ApplyEdit(session => session.SetFieldBase(paramId, bas, catalog));
        }

        /// <summary>
        /// Production rotation save. Unchanged open→save is a no-op (absent stays absent).
        /// A real membership/order edit writes the list; empty working order writes [].
        /// </summary>
        internal void RotationSaveCore(IReadOnlyList<string> pageKeysInOrder)
        {
            // Test seam: when dirty-tracking is inactive (direct core call), treat as edit.
            bool dirty = _rotationDirty || _rotationWorkingOrder == null;
            if (!dirty)
                return;
            if (pageKeysInOrder == null)
                return;

            var refs = new List<PageRef>();
            for (int i = 0; i < pageKeysInOrder.Count; i++)
            {
                var pr = PageRefFromKey(pageKeysInOrder[i]);
                if (pr != null)
                    refs.Add(pr);
            }
            ApplyEdit(session => session.SetPageOrder(refs));
            _rotationDirty = false;
            _rotationWasAbsent = false;
        }

        /// <summary>Test seam: MoveOverride via session (ladder reorder affordance).</summary>
        internal void MoveOverrideCore(ushort paramId, int fromIndex, int toIndex)
        {
            var catalog = _catalog;
            ApplyEdit(session => session.MoveOverride(paramId, fromIndex, toIndex, catalog));
        }

        /// <summary>Open 5g form for create or edit.</summary>
        internal bool OpenOverrideFormCore(ushort paramId, string overrideId, bool isNew)
        {
            if (_model == null)
                return false;
            _ovParamId = paramId;
            _ovOverrideId = overrideId;
            _ovIsNew = isNew;
            _ovEnabled = true;

            string fieldName = paramId.ToString(CultureInfo.InvariantCulture);
            PagesFieldsFieldSectionModel section = FindSection(paramId);
            if (section != null)
                fieldName = section.DisplayName;
            // Law 10: the form's suffix input clamps like the base-block one.
            txtOvSuffixContent.MaxLength = SuffixInputMaxLength(section?.SuffixWidth);

            txtOvFieldName.Text = fieldName.ToUpperInvariant();
            txtOvPageBadge.Text = _model.SelectedPage?.Badge ?? string.Empty;
            txtFormLeaf.Text = fieldName.ToUpperInvariant();
            // D15: third leaf only while modal open.
            txtFormLeafSep.Visibility = Visibility.Visible;
            txtFormLeaf.Visibility = Visibility.Visible;

            _suppressEvents = true;
            try
            {
                // Defaults for create; edit path overwrites from the live override.
                // Bring-up presentation + raw ms reset every open (dirty clears).
                _ovBringUpDurationMs = Lifetime.DefaultDurationMs;
                _ovBringUpDurationDirty = false;
                _ovBringUpSeconds = PresentationSeconds(_ovBringUpDurationMs);
                chkOvValue.IsChecked = false;
                chkOvSuffix.IsChecked = true;
                txtOvSuffixContent.Text = string.Empty;
                radAlignLeft.IsChecked = true;
                chkOvEntrypoint.IsChecked = false;
                radBringUpPin.IsChecked = true;
                txtOvSourcePath.Text = string.Empty;
                txtOvValue.Text = string.Empty;
                _ovSourceKind = ValueSourceKind.SimHubProperty;
                cmbOvOperator.SelectedIndex = 0;

                if (!isNew && !string.IsNullOrEmpty(overrideId))
                {
                    // Hydrate EVERY authored member the form edits from the live document
                    // (not the projected row summary) so open→save is byte-identical.
                    var live = _host?.GetDisplayConfigV2();
                    FieldOverride existing = null;
                    if (live != null
                        && FieldLadderMap.TryFindOverride(
                            live, _catalog, paramId, overrideId, out existing)
                        && existing != null)
                    {
                        HydrateOverrideForm(existing);
                    }
                }

                // Home seat rank for bring-up explainer + lifetime.
                _ovHomeRank = 1;
                _ovHomeRowId = null;
                if (_model.Entrypoints.Count > 0)
                {
                    _ovHomeRank = _model.Entrypoints[0].Rank;
                    _ovHomeRowId = _model.Entrypoints[0].RowId;
                }
                // Restore bring-up lifetime from the home seat when present.
                // Exact DurationMs is preserved; seconds field is presentation-only.
                if (!string.IsNullOrEmpty(_ovHomeRowId))
                {
                    var live = _host?.GetDisplayConfigV2();
                    var row = FindPriorityRow(live, _ovHomeRowId);
                    if (row?.BringUpLifetime != null)
                    {
                        if (row.BringUpLifetime.Kind == LifetimeKind.ForDuration)
                        {
                            radBringUpVisit.IsChecked = true;
                            if (row.BringUpLifetime.DurationMsPresent)
                            {
                                _ovBringUpDurationMs = row.BringUpLifetime.DurationMs;
                                _ovBringUpDurationDirty = false;
                                _ovBringUpSeconds = PresentationSeconds(_ovBringUpDurationMs);
                            }
                        }
                        else
                        {
                            radBringUpPin.IsChecked = true;
                        }
                    }
                }
                SyncBringUpSecondsBox();
                UpdateOverrideValueVisibility();
                txtBringUpExplainer.Text = DisplayCopy.BringUpExplainer(
                    _model.SelectedPage?.Name ?? string.Empty, _ovHomeRank);
            }
            finally
            {
                _suppressEvents = false;
            }

            btnOvDelete.Visibility = isNew ? Visibility.Collapsed : Visibility.Visible;
            btnOvSplit.Visibility = CanSplitCurrentOverride()
                ? Visibility.Visible
                : Visibility.Collapsed;
            ConstrainOverrideModal();
            popupOverride.IsOpen = true;
            return true;
        }

        /// <summary>Surface B plain door: choose the first authored/catalog field and open 5g.</summary>
        internal bool OpenFirstOverrideFormCore()
        {
            if (_model == null)
                return false;
            var fields = _catalog?.Itm?.Fields;
            if (fields != null)
            {
                for (int i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];
                    if (field != null && field.ParamId != 0 && field.Overridable != false)
                        return OpenOverrideFormCore(field.ParamId, null, isNew: true);
                }
            }
            var authored = _host?.GetDisplayConfigV2()?.Fields;
            if (authored != null)
            {
                foreach (var field in authored)
                    return OpenOverrideFormCore(field.Key, null, isNew: true);
            }
            return false;
        }

        /// <summary>
        /// Restore form chrome from a live override: writes, content, alignment,
        /// condition, lifetime flag (entrypoint), enabled (preserved off-form).
        /// </summary>
        private void HydrateOverrideForm(FieldOverride ov)
        {
            if (ov == null) return;

            _ovEnabled = ov.Enabled;

            bool writeValue = ov.Writes == FieldWrites.Value || ov.Writes == FieldWrites.Both;
            bool writeSuffix = ov.Writes == FieldWrites.Suffix || ov.Writes == FieldWrites.Both
                || ov.Writes == FieldWrites.Unknown;
            chkOvValue.IsChecked = writeValue;
            chkOvSuffix.IsChecked = writeSuffix;

            txtOvSuffixContent.Text = ov.Content?.Text ?? string.Empty;

            if (ov.Alignment == FieldAlignment.Right)
                radAlignRight.IsChecked = true;
            else
                radAlignLeft.IsChecked = true;

            chkOvEntrypoint.IsChecked = ov.ActsAsEntrypoint;

            var src = ov.Condition?.Source;
            if (src != null)
            {
                _ovSourceKind = src.Kind == ValueSourceKind.Unknown
                    ? ValueSourceKind.SimHubProperty
                    : src.Kind;
                txtOvSourcePath.Text = src.Name ?? string.Empty;
            }

            if (ov.Condition?.Value != null)
            {
                txtOvValue.Text = ov.Condition.Value.Value.ToString(
                    CultureInfo.InvariantCulture);
            }

            SelectOverrideOperator(ov.Condition?.Operator ?? ConditionOperator.LessThan);
        }

        private void SelectOverrideOperator(ConditionOperator op)
        {
            string phrase = DisplayCopy.OperatorPhrase(op);
            if (string.IsNullOrEmpty(phrase))
                phrase = DisplayCopy.OpBelow;
            for (int i = 0; i < cmbOvOperator.Items.Count; i++)
            {
                if (string.Equals(cmbOvOperator.Items[i] as string, phrase, StringComparison.Ordinal))
                {
                    cmbOvOperator.SelectedIndex = i;
                    return;
                }
            }
        }

        /// <summary>
        /// Map a form label (DisplayCopy / ConditionSentence vocabulary) back to the
        /// schema operator. Roster is the ConditionOperator enum (EnumText source of truth).
        /// </summary>
        private static ConditionOperator OperatorFromFormLabel(string opText)
        {
            if (string.IsNullOrEmpty(opText))
                return ConditionOperator.LessThan;
            foreach (ConditionOperator op in Enum.GetValues(typeof(ConditionOperator)))
            {
                if (op == ConditionOperator.Unknown)
                    continue;
                if (string.Equals(DisplayCopy.OperatorPhrase(op), opText, StringComparison.Ordinal))
                    return op;
            }
            return ConditionOperator.LessThan;
        }

        private void OvOperator_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents) return;
            UpdateOverrideValueVisibility();
        }

        /// <summary>Boolean operators hide the value input; level ops show it.</summary>
        private void UpdateOverrideValueVisibility()
        {
            if (txtOvValue == null || cmbOvOperator == null) return;
            var op = OperatorFromFormLabel(cmbOvOperator.SelectedItem as string);
            bool isBool = op == ConditionOperator.IsTrue || op == ConditionOperator.IsFalse;
            txtOvValue.Visibility = isBool ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>Test seam: select an override operator by schema enum.</summary>
        internal void SelectOverrideOperatorForTest(ConditionOperator op)
        {
            SelectOverrideOperator(op);
            UpdateOverrideValueVisibility();
        }

        /// <summary>Test seam: production picker-result path for the override source.</summary>
        internal void OverridePickerResultCore(string path, PropertyKind kind)
        {
            txtOvSourcePath.Text = path ?? string.Empty;
            _ovSourceKind = ToValueSourceKind(kind);
        }

        /// <summary>Test seam: production picker-result path for the field base source.</summary>
        internal void BasePickerResultCore(ushort paramId, string path, PropertyKind kind)
        {
            var section = FindSection(paramId);
            string format = section?.BaseBlock?.Format;
            string suffix = section?.BaseBlock?.BaseSuffix ?? string.Empty;
            CommitFieldBase(paramId, ToValueSourceKind(kind), path, format, suffix);
        }

        /// <summary>
        /// Test seam: user edited the bring-up seconds field (marks dirty, selects visit).
        /// </summary>
        internal void SetBringUpSecondsEditedForTest(int seconds)
        {
            _ovBringUpDurationDirty = true;
            _ovBringUpSeconds = Math.Max(1, seconds);
            if (_txtOvBringUpSeconds != null)
                _txtOvBringUpSeconds.Text = _ovBringUpSeconds.ToString(CultureInfo.InvariantCulture);
            radBringUpVisit.IsChecked = true;
        }

        /// <summary>Test seam: toggle the entrypoint flag on the open form.</summary>
        internal void SetOverrideEntrypointForTest(bool acts)
            => chkOvEntrypoint.IsChecked = acts;

        private static ValueSourceKind ToValueSourceKind(PropertyKind kind)
            => kind == PropertyKind.BuiltIn
                ? ValueSourceKind.BuiltIn
                : ValueSourceKind.SimHubProperty;

        private static PriorityRow FindPriorityRow(DisplayConfigV2 config, string rowId)
        {
            if (config?.Priority?.Rows == null || string.IsNullOrEmpty(rowId))
                return null;
            for (int i = 0; i < config.Priority.Rows.Count; i++)
            {
                var r = config.Priority.Rows[i];
                if (r != null && string.Equals(r.Id, rowId, StringComparison.Ordinal))
                    return r;
            }
            return null;
        }

        // ── Static copy ──────────────────────────────────────────────────

        private void ApplyStaticCopy()
        {
            txtTitle.Text = DisplayCopy.PagesAndFields;
            txtDivider.Text = DisplayCopy.ModeProfileDivider;
            txtPreviewWatermark.Text = DisplayCopy.PreviewWatermark;
            txtFromCatalog.Text = DisplayCopy.FromCatalogBadge;
            txtWhereLabel.Text = DisplayCopy.WhereThisApplies;
            txtThisWheelLabel.Text = DisplayCopy.ThisWheel;
            txtThisPageLabel.Text = DisplayCopy.ThisPageCard;
            txtEpLabel.Text = DisplayCopy.EntrypointsToThisPage;
            txtEpFoot.Text = DisplayCopy.PageRanksNothingAbove;
            SetHyperlinkText(linkPriority, DisplayCopy.PrioritySpoke);
            SetHyperlinkText(linkShowAll, DisplayCopy.ShowAllFields);

            txtOvTitlePrefix.Text = DisplayCopy.AnOverrideOn;
            txtWhatItWrites.Text = DisplayCopy.WhatItWrites;
            chkOvValue.Content = DisplayCopy.TheValue;
            chkOvSuffix.Content = DisplayCopy.TheSuffix;
            txtAlignLabel.Text = DisplayCopy.AlignLabel;
            radAlignLeft.Content = DisplayCopy.AlignLeft;
            radAlignRight.Content = DisplayCopy.AlignRight;
            txtValueThenSuffix.Text = DisplayCopy.ValueThenSuffixNote;
            txtWhen.Text = DisplayCopy.When;
            chkOvEntrypoint.Content = DisplayCopy.EntrypointFlag;
            radBringUpPin.Content = DisplayCopy.BringUpStaysWhileActive;
            EnsureBringUpSecondsChrome();
            btnOvDelete.Content = DisplayCopy.Delete;
            btnOvSplit.Content = DisplayCopy.GiveThisOverrideItsOwnPriority;
            btnOvCancel.Content = DisplayCopy.Cancel;
            btnOvSave.Content = DisplayCopy.Save;
            txtOvChevron.Text = DisplayCopy.PropertyRowChevron;

            txtRotationTitle.Text = DisplayCopy.ReorderTheRotation;
            txtInRotation.Text = DisplayCopy.InTheRotation;
            txtNotInRotation.Text = DisplayCopy.NotInTheRotation;
            txtDragNote.Text = DisplayCopy.DragBetweenListsNote;
            btnRotCancel.Content = DisplayCopy.Cancel;
            btnRotSave.Content = DisplayCopy.SaveOrder;

            // Full ConditionOperator roster (EnumText source of truth), labels via
            // DisplayCopy.OperatorPhrase (ConditionSentence vocabulary). Unknown skipped.
            cmbOvOperator.SelectionChanged -= OvOperator_SelectionChanged;
            cmbOvOperator.Items.Clear();
            foreach (ConditionOperator op in Enum.GetValues(typeof(ConditionOperator)))
            {
                if (op == ConditionOperator.Unknown)
                    continue;
                string phrase = DisplayCopy.OperatorPhrase(op);
                if (string.IsNullOrEmpty(phrase))
                    continue;
                cmbOvOperator.Items.Add(phrase);
            }
            cmbOvOperator.SelectedIndex = 0;
            cmbOvOperator.SelectionChanged += OvOperator_SelectionChanged;
            UpdateOverrideValueVisibility();
        }

        /// <summary>
        /// Build the "for N s each time it fires" visit radio with an editable seconds box.
        /// Presentation-only; raw DurationMs is tracked separately until the user edits.
        /// </summary>
        private void EnsureBringUpSecondsChrome()
        {
            if (_txtOvBringUpSeconds == null)
            {
                _txtOvBringUpSeconds = new TextBox
                {
                    Width = 36,
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1C))),
                    Foreground = WhiteFg,
                    BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47))),
                    FontSize = 12.5,
                };
                _txtOvBringUpSeconds.TextChanged += (s, e) =>
                {
                    if (_suppressEvents) return;
                    if (int.TryParse(_txtOvBringUpSeconds.Text, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int sec) && sec > 0)
                    {
                        _ovBringUpDurationDirty = true;
                        _ovBringUpSeconds = sec;
                    }
                };
                // No LostFocus handler: TextChanged (suppress-guarded) already
                // captures every real edit; a focus-without-edit must never dirty
                // the raw milliseconds (2500ms survives focusing the shown "3").
            }

            var label = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // "for {n} s each time it fires" — n is the editable box.
            string template = DisplayCopy.BringUpForSeconds(0);
            int nIdx = template.IndexOf('0');
            string prefix = nIdx >= 0 ? template.Substring(0, nIdx) : "for ";
            string suffix = nIdx >= 0 && nIdx + 1 <= template.Length
                ? template.Substring(nIdx + 1)
                : " s each time it fires";
            label.Children.Add(new TextBlock
            {
                Text = prefix,
                FontSize = 12.5,
                Foreground = BodyFg,
                VerticalAlignment = VerticalAlignment.Center,
            });
            label.Children.Add(_txtOvBringUpSeconds);
            label.Children.Add(new TextBlock
            {
                Text = suffix,
                FontSize = 12.5,
                Foreground = BodyFg,
                VerticalAlignment = VerticalAlignment.Center,
            });
            radBringUpVisit.Content = label;
            SyncBringUpSecondsBox();
        }

        private void SyncBringUpSecondsBox()
        {
            if (_txtOvBringUpSeconds == null) return;
            bool was = _suppressEvents;
            _suppressEvents = true;
            try
            {
                _txtOvBringUpSeconds.Text = Math.Max(1, _ovBringUpSeconds)
                    .ToString(CultureInfo.InvariantCulture);
            }
            finally
            {
                _suppressEvents = was;
            }
        }

        private void ApplyModel(DisplayPagesFieldsV2Model model)
        {
            if (model == null)
                return;

            txtSurfaceWord.Text = model.SurfaceWord;
            txtSituation.Text = model.SituationCopy;
            dotSituation.Fill = model.InGame
                ? Freeze(new SolidColorBrush(Color.FromRgb(0x35, 0xE0, 0x6A)))
                : Freeze(new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x8F)));

            if (!model.ShowContent)
            {
                panelContent.Visibility = Visibility.Collapsed;
                txtModeOffEmpty.Visibility = Visibility.Visible;
                txtModeOffEmpty.Text = model.ModeOffEmptyState ?? string.Empty;
                return;
            }
            panelContent.Visibility = Visibility.Visible;
            txtModeOffEmpty.Visibility = Visibility.Collapsed;

            // Focus-clear announcement
            if (!string.IsNullOrEmpty(_focusClearAnnouncement))
            {
                bannerFocusClear.Visibility = Visibility.Visible;
                txtFocusClear.Text = _focusClearAnnouncement;
            }
            else
            {
                bannerFocusClear.Visibility = Visibility.Collapsed;
            }

            txtStripNote.Text = model.StripNote ?? string.Empty;
            txtPreviewCaption.Text = model.PreviewCaption ?? string.Empty;
            txtWhereBody.Text = model.WhereThisAppliesBody ?? string.Empty;
            txtEpCount.Text = model.EntrypointsCountLabel ?? string.Empty;

            if (!string.IsNullOrEmpty(model.ThisWheelBody))
            {
                cardThisWheel.Visibility = Visibility.Visible;
                txtThisWheelBody.Text = model.ThisWheelBody;
            }
            else
            {
                cardThisWheel.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrEmpty(model.ThisPageBody))
            {
                cardThisPage.Visibility = Visibility.Visible;
                txtThisPageBody.Text = model.ThisPageBody;
            }
            else
            {
                cardThisPage.Visibility = Visibility.Collapsed;
            }

            // Filter state line — D3/D4: absent when showing all fields.
            if (model.IsFiltered && !string.IsNullOrEmpty(model.FilterStateLine))
            {
                barFilterState.Visibility = Visibility.Visible;
                // State line includes "— Show all fields"; show the prefix, link is separate.
                string line = model.FilterStateLine;
                string marker = " — " + DisplayCopy.ShowAllFields;
                int idx = line.LastIndexOf(marker, StringComparison.Ordinal);
                runFilterText.Text = idx >= 0 ? line.Substring(0, idx) + " — " : line + " ";
            }
            else
            {
                barFilterState.Visibility = Visibility.Collapsed;
            }

            // The preview face renders every poll — live values must flow — while
            // the structural rebuilds below stay signature-gated.
            UpdatePreviewFace(model);

            // Rebuilds tear down every child (buttons mid-click, the focused suffix
            // box) — gate them on what they actually draw, not on poll cadence.
            string sig = BuildStructureSignature(model);
            if (string.Equals(sig, _lastStructureSignature, StringComparison.Ordinal))
                return;
            _lastStructureSignature = sig;

            RebuildPageStrip(model);
            RebuildEntrypoints(model);
            RebuildFieldCollection(model);
        }

        /// <summary>
        /// The preview face for the selected page. ITM pages render on the digital
        /// twin: live values while the wheel is synced on that very page, otherwise
        /// the page layout with hardware placeholders + authored base suffixes.
        /// Hosted pages render the 3-character segment face — the surface their
        /// content actually writes (the ITM screen parks on Legacy meanwhile;
        /// the caption states it).
        /// </summary>
        private void UpdatePreviewFace(DisplayPagesFieldsV2Model model)
        {
            bool segment = model.SelectedPage != null && !model.SelectedPage.IsItm;
            hostSegmentPreview.Visibility = segment
                ? Visibility.Visible
                : Visibility.Collapsed;
            displayMirror.Visibility = segment
                ? Visibility.Collapsed
                : Visibility.Visible;
            if (segment)
            {
                // Live face bytes are not published on the snapshot (pre-existing
                // gap, same as Priority's 5j face) — blank frame, honest empty.
                _segmentPreviewFace.Render(null);
                return;
            }

            ItmPage? page = model.SelectedPage != null
                ? FanaBridge.Display.Arbitration.CatalogPageIdAdapter.ToItmPage(
                    model.SelectedPage.CatalogPageId)
                : null;
            var values = model.Values;
            bool liveMatch = page.HasValue
                && values != null
                && values.State == ItmLifecycleState.Synced
                && values.Page == page.Value;
            if (liveMatch)
            {
                displayMirror.Render(ItmDisplayMirrorRender.Build(
                    values, model.FocusedParamId, interactive: true));
            }
            else if (page.HasValue)
            {
                displayMirror.Render(ItmDisplayMirrorRender.BuildLayout(
                    page.Value, model.FocusedParamId, interactive: true,
                    authoredSuffixes: AuthoredSuffixMap(model)));
            }
            else
            {
                displayMirror.Render((DisplayValuesSnapshot)null);
            }
        }

        /// <summary>Authored base suffix per param for the selected page's sections.</summary>
        private static Dictionary<ushort, string> AuthoredSuffixMap(
            DisplayPagesFieldsV2Model model)
        {
            var map = new Dictionary<ushort, string>();
            for (int g = 0; g < model.ScopeGroups.Count; g++)
                CollectSuffixes(map, model.ScopeGroups[g].Sections);
            CollectSuffixes(map, model.FlatSections);
            return map;
        }

        private static void CollectSuffixes(
            Dictionary<ushort, string> map,
            IReadOnlyList<PagesFieldsFieldSectionModel> sections)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section?.BaseBlock == null)
                    continue;
                string suffix = section.BaseBlock.BaseSuffix;
                if (!string.IsNullOrWhiteSpace(suffix))
                    map[section.ParamId] = suffix;
            }
        }

        private string _lastStructureSignature;

        /// <summary>
        /// Serializes every model fact the four Rebuild* methods render (plus the
        /// pane width the card layout derives from). Unit separator between fields;
        /// any drawn fact missing here would go stale — extend when a rebuild grows.
        /// </summary>
        private string BuildStructureSignature(DisplayPagesFieldsV2Model model)
        {
            const char S = '\x1F';
            var sb = new System.Text.StringBuilder(1024);
            sb.Append(model.ShowContent ? '1' : '0').Append(S)
                .Append(model.LegacyOnly ? '1' : '0').Append(S)
                .Append(model.IsFiltered ? '1' : '0').Append(S)
                .Append(_focusClearAnnouncement).Append(S)
                .Append((int)(dockFieldRegion?.ActualWidth ?? 0)).Append(S);

            var pages = model.PageButtons;
            for (int i = 0; i < pages.Count; i++)
            {
                var p = pages[i];
                if (p == null) continue;
                sb.Append(p.Key).Append(S).Append(p.Name).Append(S)
                    .Append(p.Badge).Append(S)
                    .Append(p.IsItm ? '1' : '0')
                    .Append(p.IsSelected ? '1' : '0')
                    .Append(p.IsDimmed ? '1' : '0').Append(S);
            }
            sb.Append('#');

            var eps = model.Entrypoints;
            for (int i = 0; i < eps.Count; i++)
            {
                var ep = eps[i];
                if (ep == null) continue;
                sb.Append(ep.Rank).Append(S).Append(ep.Detail).Append(S)
                    .Append(ep.StatusCopy).Append(S)
                    .Append(ep.IsWinner ? '1' : '0').Append(S);
            }
            sb.Append('#');

            var groups = model.ScopeGroups;
            for (int g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                if (group == null) continue;
                sb.Append(group.Header).Append(S);
                AppendSectionsSignature(sb, group.Sections, S);
            }
            sb.Append('#');
            AppendSectionsSignature(sb, model.FlatSections, S);
            return sb.ToString();
        }

        private static void AppendSectionsSignature(
            System.Text.StringBuilder sb,
            IReadOnlyList<PagesFieldsFieldSectionModel> sections,
            char s)
        {
            for (int i = 0; i < sections.Count; i++)
            {
                var sec = sections[i];
                if (sec == null) continue;
                sb.Append(sec.ParamId).Append(s).Append(sec.DisplayName).Append(s)
                    .Append(sec.CapabilityHint).Append(s).Append(sec.ReachLine).Append(s)
                    .Append(sec.IsProvisional ? '1' : '0')
                    .Append(sec.IsLocked ? '1' : '0')
                    .Append(sec.IsInertCollision ? '1' : '0').Append(s)
                    .Append(sec.InertReason).Append(s);
                var formats = sec.OfferedFormats;
                for (int f = 0; f < formats.Count; f++)
                    sb.Append(formats[f]).Append(s);
                var bas = sec.BaseBlock;
                if (bas != null)
                {
                    sb.Append(bas.SourceName).Append(s)
                        .Append((int)bas.SourceKind).Append(s)
                        .Append(bas.Format).Append(s)
                        .Append(bas.BaseSuffix).Append(s);
                }
                var ovs = sec.Overrides;
                for (int o = 0; o < ovs.Count; o++)
                {
                    var ov = ovs[o];
                    if (ov == null) continue;
                    sb.Append(ov.OverrideId).Append(s).Append(ov.Rank).Append(s)
                        .Append(ov.WritesChip).Append(s).Append(ov.ContentChip).Append(s)
                        .Append(ov.ConditionSentence).Append(s)
                        .Append(ov.ActsAsEntrypoint ? '1' : '0')
                        .Append(ov.Enabled ? '1' : '0')
                        .Append(ov.Degraded ? '1' : '0').Append(s);
                }
                sb.Append(';');
            }
        }

        private void RebuildPageStrip(DisplayPagesFieldsV2Model model)
        {
            panelPageStrip.Children.Clear();
            bool sawHosted = false;
            bool sawItm = false;
            for (int i = 0; i < model.PageButtons.Count; i++)
            {
                var p = model.PageButtons[i];
                if (p == null) continue;

                // Divider between ITM and hosted.
                if (!p.IsItm && sawItm && !sawHosted)
                {
                    panelPageStrip.Children.Add(new Border
                    {
                        Width = 1,
                        Height = 36,
                        Background = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C))),
                        Margin = new Thickness(5, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    sawHosted = true;
                }
                if (p.IsItm) sawItm = true;

                var btn = new Button
                {
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 6, 4),
                    Cursor = Cursors.Hand,
                    Tag = p.Key,
                    Background = p.IsSelected ? SelectedPageBrush : CardBg,
                    BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47))),
                    BorderThickness = new Thickness(1),
                    Opacity = model.LegacyOnly && p.IsItm ? 0.45 : 1.0,
                };
                if (model.LegacyOnly && p.IsItm)
                    btn.ToolTip = DisplayCopy.CantRunHere;

                var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(new TextBlock
                {
                    Text = p.Badge,
                    FontSize = 9,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = p.IsSelected
                        ? Freeze(new SolidColorBrush(Color.FromRgb(0xCF, 0xE6, 0xF5)))
                        : MutedFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                stack.Children.Add(new TextBlock
                {
                    Text = p.Name,
                    FontSize = 13,
                    FontWeight = p.IsSelected ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = WhiteFg,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                btn.Content = stack;
                string key = p.Key;
                btn.Click += (s, e) => SelectPageCore(key);
                panelPageStrip.Children.Add(btn);
            }

            // + Add a page tile → Surface B (A-N5; host wires AddPageRequested).
            var add = new Button
            {
                Content = DisplayCopy.AddAPage,
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 6, 4),
                Background = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x25))),
                BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47))),
                BorderThickness = new Thickness(1),
                Foreground = BodyFg,
                FontSize = 12.5,
                Cursor = Cursors.Hand,
            };
            add.Click += (s, e) => AddPageRequested?.Invoke(this, EventArgs.Empty);
            panelPageStrip.Children.Add(add);
        }

        private void RebuildEntrypoints(DisplayPagesFieldsV2Model model)
        {
            panelEntrypoints.Children.Clear();
            for (int i = 0; i < model.Entrypoints.Count; i++)
            {
                var ep = model.Entrypoints[i];
                if (ep == null) continue;
                var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });

                var row = new Border
                {
                    Background = ep.IsWinner ? WinnerBg : Brushes.Transparent,
                    Padding = new Thickness(ep.IsWinner ? 6 : 0, 4, 4, 4),
                    // F1: winner = navy + 3 px inset accent bar (structural, not ● showing).
                    BorderBrush = ep.IsWinner ? AccentBrush : Brushes.Transparent,
                    BorderThickness = ep.IsWinner ? new Thickness(3, 0, 0, 0) : new Thickness(0),
                };

                var inner = new Grid();
                inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });

                var rank = new TextBlock
                {
                    Text = ep.Rank.ToString(CultureInfo.InvariantCulture),
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    Foreground = MutedFg,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(rank, 0);
                inner.Children.Add(rank);

                var detail = new TextBlock
                {
                    Text = ep.Detail ?? string.Empty,
                    FontSize = 12.5,
                    Foreground = BodyFg,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(detail, 1);
                inner.Children.Add(detail);

                // F1: empty status for winner (OnScreen = "").
                var status = new TextBlock
                {
                    Text = ep.StatusCopy ?? string.Empty,
                    FontSize = 11.5,
                    Foreground = MutedFg,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(status, 2);
                inner.Children.Add(status);

                row.Child = inner;
                panelEntrypoints.Children.Add(row);
            }
        }

        private void RebuildFieldCollection(DisplayPagesFieldsV2Model model)
        {
            // Clearing collapses the ScrollViewer extent; keep the reading position.
            double keepOffset = scrollFieldCollection?.VerticalOffset ?? 0;

            panelFieldCollection.Children.Clear();

            if (model.ScopeGroups.Count > 0)
            {
                for (int g = 0; g < model.ScopeGroups.Count; g++)
                {
                    var group = model.ScopeGroups[g];
                    panelFieldCollection.Children.Add(new TextBlock
                    {
                        Text = group.Header,
                        FontSize = 9.5,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = MutedFg,
                        Margin = new Thickness(0, g == 0 ? 0 : 12, 0, 6),
                    });
                    panelFieldCollection.Children.Add(BuildSectionGrid(group.Sections, model));
                }
            }
            else
            {
                // Flat collection (no catalog).
                panelFieldCollection.Children.Add(BuildSectionGrid(model.FlatSections, model));
            }

            if (keepOffset > 0)
                scrollFieldCollection.ScrollToVerticalOffset(keepOffset);
        }

        private UIElement BuildSectionGrid(
            IReadOnlyList<PagesFieldsFieldSectionModel> sections,
            DisplayPagesFieldsV2Model model)
        {
            // D12: two cards must fit the collection pane. Budget from the dock width
            // (min 360); A-O5: focused section keeps cell width (no reflow to full).
            double paneWidth = dockFieldRegion?.ActualWidth ?? 0;
            if (paneWidth < 100)
                paneWidth = 400;
            // Two cards + gutters: floor so 2 × cell + 10 always fit.
            double cellWidth = model.IsFiltered
                ? Math.Min(280, paneWidth - 16)
                : Math.Max(160, Math.Floor((paneWidth - 20) / 2.0));

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < sections.Count; i++)
            {
                var section = sections[i];
                if (section == null) continue;
                var card = BuildFieldSection(section);
                card.Width = cellWidth;
                card.Margin = new Thickness(0, 0, 10, 10);
                wrap.Children.Add(card);
            }
            return wrap;
        }

        private Border BuildFieldSection(PagesFieldsFieldSectionModel section)
        {
            var card = new Border
            {
                Background = CardBg,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(10, 8, 10, 10),
                Opacity = section.IsInertCollision ? 0.55 : 1.0,
            };

            var stack = new StackPanel();

            // Section header — dual-channel focus target (A-N7).
            var header = new DockPanel { Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 0, 6) };
            var titleCol = new StackPanel();
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
            nameRow.Children.Add(new TextBlock
            {
                Text = section.DisplayName,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = WhiteFg,
            });
            if (section.IsProvisional)
            {
                nameRow.Children.Add(new Border
                {
                    Background = Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x20))),
                    BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x4A, 0x32))),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(4, 0, 4, 0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Child = new TextBlock
                    {
                        Text = DisplayCopy.FromCatalogBadge,
                        FontSize = 9,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xA9, 0x5F))),
                    },
                });
            }
            titleCol.Children.Add(nameRow);
            if (!string.IsNullOrEmpty(section.CapabilityHint))
            {
                titleCol.Children.Add(new TextBlock
                {
                    Text = section.CapabilityHint,
                    FontSize = 11,
                    Foreground = MutedFg,
                });
            }
            if (!string.IsNullOrEmpty(section.ReachLine))
            {
                // D9: reach on every shared section.
                titleCol.Children.Add(new TextBlock
                {
                    Text = section.ReachLine,
                    FontSize = 11,
                    Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0x9F, 0xBD, 0xD4))),
                });
            }
            if (section.IsInertCollision && !string.IsNullOrEmpty(section.InertReason))
            {
                titleCol.Children.Add(new TextBlock
                {
                    Text = section.InertReason,
                    FontSize = 11,
                    Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0xC9, 0xA9, 0x5F))),
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            if (!section.IsInertCollision && !section.IsLocked)
            {
                var addBtn = new Button
                {
                    Content = DisplayCopy.AddAnOverride,
                    FontSize = 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Padding = new Thickness(8, 3, 8, 3),
                    Background = Freeze(new SolidColorBrush(Color.FromRgb(0x58, 0x58, 0x5A))),
                    BorderThickness = new Thickness(0),
                    Foreground = WhiteFg,
                    Cursor = Cursors.Hand,
                    VerticalAlignment = VerticalAlignment.Top,
                    MinWidth = 126,
                };
                DockPanel.SetDock(addBtn, Dock.Right);
                ushort addParam = section.ParamId;
                addBtn.Click += (s, e) =>
                {
                    e.Handled = true;
                    OpenOverrideFormCore(addParam, null, isNew: true);
                };
                header.Children.Add(addBtn);
            }
            // The right-docked action must be added before the fill child. Otherwise
            // DockPanel treats the action as LastChildFill and clips its ruled label.
            header.Children.Add(titleCol);

            ushort focusParam = section.ParamId;
            header.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                SelectFieldCore(focusParam);
            };
            stack.Children.Add(header);

            // Override ladder rows.
            if (!section.IsInertCollision)
            {
                for (int i = 0; i < section.Overrides.Count; i++)
                {
                    var ov = section.Overrides[i];
                    if (ov == null) continue;
                    stack.Children.Add(BuildOverrideRow(section.ParamId, ov));
                }

                // BASE pinned block.
                stack.Children.Add(BuildBaseBlock(section));
            }

            card.Child = stack;
            return card;
        }

        private UIElement BuildOverrideRow(ushort paramId, PagesFieldsOverrideRowModel ov)
        {
            var border = new Border
            {
                Background = Brushes.Transparent,
                Padding = new Thickness(2, 4, 2, 4),
                Cursor = Cursors.Hand,
                ToolTip = DisplayCopy.OpenThisOverridesForm,
                Margin = new Thickness(0, 0, 0, 2),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });

            // Reorder affordance (Priority grip idiom → up/down; ladder click still opens form).
            int rankIndex = ov.Rank - 1;
            var reorder = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            reorder.Children.Add(new TextBlock
            {
                Text = DisplayCopy.GripGlyph,
                FontSize = 11,
                Foreground = MutedFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
            });
            var up = MakeTinyButton("↑", () =>
            {
                if (rankIndex > 0)
                    MoveOverrideCore(paramId, rankIndex, rankIndex - 1);
            });
            var down = MakeTinyButton("↓", () =>
            {
                MoveOverrideCore(paramId, rankIndex, rankIndex + 1);
            });
            reorder.Children.Add(up);
            reorder.Children.Add(down);
            Grid.SetColumn(reorder, 0);
            grid.Children.Add(reorder);

            grid.Children.Add(Cell(
                ov.Rank.ToString(CultureInfo.InvariantCulture), 1, MutedFg, 12));

            var writes = new StackPanel { Orientation = Orientation.Horizontal };
            writes.Children.Add(new Border
            {
                Background = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1C))),
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 4, 0),
                Child = new TextBlock
                {
                    Text = ov.WritesChip,
                    FontSize = 10,
                    Foreground = BodyFg,
                },
            });
            if (!string.IsNullOrEmpty(ov.ContentChip))
            {
                writes.Children.Add(new Border
                {
                    Background = Brushes.Black,
                    Padding = new Thickness(4, 1, 4, 1),
                    Child = new TextBlock
                    {
                        Text = ov.ContentChip,
                        FontSize = 10,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0x7F, 0xD0, 0xF5))),
                    },
                });
            }
            Grid.SetColumn(writes, 2);
            grid.Children.Add(writes);

            grid.Children.Add(new TextBlock
            {
                Text = ov.ConditionSentence ?? string.Empty,
                FontSize = 11.5,
                Foreground = BodyFg,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            }.WithColumn(3));

            // F4: bare ↑ glyph only (never inline "acts as an entrypoint").
            if (ov.ActsAsEntrypoint)
            {
                var glyph = new TextBlock
                {
                    Text = DisplayCopy.EntrypointGlyph,
                    FontSize = 14,
                    Foreground = Freeze(new SolidColorBrush(Color.FromRgb(0x9F, 0xBD, 0xD4))),
                    ToolTip = DisplayCopy.EntrypointTooltip,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(glyph, 4);
                grid.Children.Add(glyph);
            }

            border.Child = grid;
            string ovId = ov.OverrideId;
            border.MouseLeftButtonDown += (s, e) =>
            {
                // Reorder buttons set Handled; row click opens the form.
                if (e.Handled) return;
                e.Handled = true;
                OpenOverrideFormCore(paramId, ovId, isNew: false);
            };
            return border;
        }

        /// <summary>
        /// Suffix input clamp: measured width when known, wire ceiling otherwise.
        /// Width 0 (no region) keeps the ceiling — the composer gates it anyway.
        /// </summary>
        private static int SuffixInputMaxLength(int? suffixWidth)
            => suffixWidth.HasValue && suffixWidth.Value > 0
                ? suffixWidth.Value
                : FanaBridge.Protocol.ItmEncoder.MaxSuffixLength;

        private Button MakeTinyButton(string label, Action onClick)
        {
            var btn = new Button
            {
                Content = label,
                FontSize = 9,
                Padding = new Thickness(2, 0, 2, 0),
                Margin = new Thickness(0, 0, 1, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = MutedFg,
                Cursor = Cursors.Hand,
                MinWidth = 12,
                MinHeight = 14,
            };
            btn.Click += (s, e) =>
            {
                e.Handled = true;
                onClick?.Invoke();
            };
            return btn;
        }

        private UIElement BuildBaseBlock(PagesFieldsFieldSectionModel section)
        {
            var border = new Border
            {
                BorderBrush = DashedBorder,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 6, 8, 8),
                Margin = new Thickness(0, 6, 0, 0),
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = DisplayCopy.BaseBlockLabel,
                FontSize = 9.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = MutedFg,
            });
            stack.Children.Add(new TextBlock
            {
                Text = DisplayCopy.BaseShowsWhenNoOverrideTrue,
                FontSize = 11,
                Foreground = MutedFg,
                Margin = new Thickness(0, 0, 0, 6),
            });

            if (section.IsLocked)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = section.BaseBlock?.SourceName ?? string.Empty,
                    FontSize = 12,
                    Foreground = BodyFg,
                });
            }
            else
            {
                // What it reads (5f property-picker pattern) / How it's written /
                // Base suffix (text input) — full sections, 8c item 2.
                ushort basParam = section.ParamId;
                var sourceKind = section.BaseBlock?.SourceKind
                    ?? ValueSourceKind.SimHubProperty;
                string basSource = section.BaseBlock?.SourceName ?? string.Empty;
                string basSuffix = section.BaseBlock?.BaseSuffix ?? string.Empty;
                string basFormat = section.BaseBlock?.Format;

                stack.Children.Add(new TextBlock
                {
                    Text = DisplayCopy.WhatItReads,
                    FontSize = 11,
                    Foreground = MutedFg,
                });
                var sourcePath = new TextBlock
                {
                    Text = string.IsNullOrEmpty(basSource) ? "—" : basSource,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                    Foreground = BodyFg,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var chevron = new TextBlock
                {
                    Text = DisplayCopy.PropertyRowChevron,
                    Foreground = MutedFg,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                DockPanel.SetDock(chevron, Dock.Right);
                var sourceDock = new DockPanel();
                sourceDock.Children.Add(chevron);
                sourceDock.Children.Add(sourcePath);
                var sourceRow = new Border
                {
                    Background = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1C))),
                    BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47))),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 2, 0, 6),
                    Cursor = Cursors.Hand,
                    Child = sourceDock,
                };
                sourceRow.MouseLeftButtonDown += (s, e) =>
                {
                    e.Handled = true;
                    if (TryPickProperty(sourcePath.Text == "—" ? string.Empty : sourcePath.Text,
                            out string picked, out PropertyKind pickedKind)
                        && !string.IsNullOrEmpty(picked))
                    {
                        sourcePath.Text = picked;
                        basSource = picked;
                        sourceKind = ToValueSourceKind(pickedKind);
                        CommitFieldBase(basParam, sourceKind, basSource, basFormat, basSuffix);
                    }
                };
                stack.Children.Add(sourceRow);

                var formatCombo = new ComboBox
                {
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 6),
                };
                for (int i = 0; i < section.OfferedFormats.Count; i++)
                    formatCombo.Items.Add(section.OfferedFormats[i]);
                if (!string.IsNullOrEmpty(basFormat)
                    && formatCombo.Items.Contains(basFormat))
                    formatCombo.SelectedItem = basFormat;
                else if (formatCombo.Items.Count > 0)
                    formatCombo.SelectedIndex = 0;

                stack.Children.Add(new TextBlock
                {
                    Text = DisplayCopy.HowItsWritten,
                    FontSize = 11,
                    Foreground = MutedFg,
                });
                formatCombo.SelectionChanged += (s, e) =>
                {
                    if (_suppressEvents) return;
                    basFormat = formatCombo.SelectedItem as string;
                    CommitFieldBase(basParam, sourceKind, basSource, basFormat, basSuffix);
                };
                stack.Children.Add(formatCombo);

                stack.Children.Add(new TextBlock
                {
                    Text = DisplayCopy.BaseSuffixLabel,
                    FontSize = 11,
                    Foreground = MutedFg,
                });
                var suffixBox = new TextBox
                {
                    Text = basSuffix,
                    FontSize = 12,
                    FontFamily = new FontFamily("Consolas"),
                    Background = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1C))),
                    Foreground = WhiteFg,
                    BorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x47))),
                    Padding = new Thickness(4, 2, 4, 2),
                    Margin = new Thickness(0, 2, 0, 2),
                    // Law 10: over-length content cannot exist — the input clamps to
                    // the measured width, else the wire ceiling.
                    MaxLength = SuffixInputMaxLength(section.SuffixWidth),
                };
                suffixBox.LostKeyboardFocus += (s, e) =>
                {
                    if (_suppressEvents) return;
                    string text = suffixBox.Text ?? string.Empty;
                    // Teardown-driven focus loss must never write; only a real
                    // user edit commits.
                    if (!suffixBox.IsLoaded
                        || string.Equals(text, basSuffix, StringComparison.Ordinal))
                        return;
                    basSuffix = text;
                    CommitFieldBase(basParam, sourceKind, basSource, basFormat, basSuffix);
                };
                suffixBox.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        basSuffix = suffixBox.Text ?? string.Empty;
                        CommitFieldBase(basParam, sourceKind, basSource, basFormat, basSuffix);
                        e.Handled = true;
                    }
                };
                stack.Children.Add(suffixBox);
                stack.Children.Add(new TextBlock
                {
                    Text = DisplayCopy.BaseSuffixNote,
                    FontSize = 11,
                    Foreground = MutedFg,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }

            // Suppression notes: when a higher-ranked timed override owns a region
            // that a lower override also writes, surface the digest consequence line.
            if (!section.IsInertCollision && section.Overrides.Count > 1)
            {
                for (int i = 0; i < section.Overrides.Count; i++)
                {
                    var upper = section.Overrides[i];
                    if (upper == null || !upper.Enabled) continue;
                    // Look for forDuration lifetime tail in the condition sentence
                    // ("for N s") — projected row carries the chip text for content.
                    for (int j = i + 1; j < section.Overrides.Count; j++)
                    {
                        var lower = section.Overrides[j];
                        if (lower == null || !lower.Enabled) continue;
                        // Same writes chip family (suffix/value) → lower is masked while upper holds.
                        if (!string.IsNullOrEmpty(upper.WritesChip)
                            && !string.IsNullOrEmpty(lower.WritesChip)
                            && (upper.WritesChip.IndexOf(DisplayCopy.WritesSuffix, StringComparison.Ordinal) >= 0
                                || upper.WritesChip.IndexOf(DisplayCopy.TheValue, StringComparison.Ordinal) >= 0)
                            && SharesWriteRegion(upper.WritesChip, lower.WritesChip))
                        {
                            int seconds = ParseDurationSeconds(upper.ConditionSentence);
                            if (seconds <= 0) seconds = 3;
                            string character = string.IsNullOrEmpty(lower.ContentChip)
                                ? "!"
                                : lower.ContentChip;
                            stack.Children.Add(new Border
                            {
                                BorderBrush = Freeze(new SolidColorBrush(
                                    Color.FromRgb(0x5A, 0x4A, 0x32))),
                                BorderThickness = new Thickness(3, 0, 0, 0),
                                Padding = new Thickness(8, 4, 4, 4),
                                Margin = new Thickness(0, 4, 0, 0),
                                Child = new TextBlock
                                {
                                    Text = DisplayCopy.SuffixSuppressedNote(seconds, character),
                                    FontSize = 11,
                                    Foreground = Freeze(new SolidColorBrush(
                                        Color.FromRgb(0xC9, 0xA9, 0x5F))),
                                    TextWrapping = TextWrapping.Wrap,
                                },
                            });
                            break;
                        }
                    }
                }
            }

            border.Child = stack;
            return border;
        }

        private void CommitFieldBase(
            ushort paramId,
            ValueSourceKind kind,
            string sourceName,
            string format,
            string suffix)
        {
            SetFieldBaseCore(paramId, new FieldBase
            {
                Source = string.IsNullOrEmpty(sourceName)
                    ? null
                    : new FanaBridge.Display.Schema2.ValueSource
                    {
                        Kind = kind == ValueSourceKind.Unknown
                            ? ValueSourceKind.SimHubProperty
                            : kind,
                        Name = sourceName,
                    },
                Format = format,
                BaseSuffix = suffix ?? string.Empty,
            });
        }

        private bool TryPickProperty(string current, out string picked, out PropertyKind kind)
        {
            picked = null;
            kind = PropertyKind.SimHubProperty;
            try
            {
                var owner = Window.GetWindow(this);
                var builtIns = BuiltInProperties.All;
                var all = _propertyCatalog != null
                    ? _propertyCatalog.GetAllPropertyNames()
                    : Array.Empty<string>();
                var mappedRoles = _roleCatalog?.GetMappedRoles()?.Roles;
                Func<string, object> valueReader = name =>
                {
                    if (_propertyCatalog != null
                        && _propertyCatalog.TryReadPropertyValue(name, out object value))
                        return value;
                    return null;
                };
                if (PropertyPickerDialog.TryPick(
                        owner, builtIns, all, mappedRoles,
                        current,
                        _pickerStore,
                        builtIns,
                        valueReader,
                        out picked,
                        out kind))
                {
                    return !string.IsNullOrEmpty(picked);
                }
            }
            catch
            {
                // Picker unavailable in headless tests — ignore.
            }
            return false;
        }

        private static bool SharesWriteRegion(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            bool aSuf = a.IndexOf(DisplayCopy.WritesSuffix, StringComparison.Ordinal) >= 0;
            bool bSuf = b.IndexOf(DisplayCopy.WritesSuffix, StringComparison.Ordinal) >= 0;
            bool aVal = a.IndexOf(DisplayCopy.TheValue, StringComparison.Ordinal) >= 0;
            bool bVal = b.IndexOf(DisplayCopy.TheValue, StringComparison.Ordinal) >= 0;
            return (aSuf && bSuf) || (aVal && bVal);
        }

        private static int ParseDurationSeconds(string conditionSentence)
        {
            if (string.IsNullOrEmpty(conditionSentence))
                return 0;
            // Lifetime tail forms like "for 3 s" / "for 5s".
            int forIdx = conditionSentence.IndexOf("for ", StringComparison.OrdinalIgnoreCase);
            if (forIdx < 0) return 0;
            int i = forIdx + 4;
            int start = i;
            while (i < conditionSentence.Length && char.IsDigit(conditionSentence[i]))
                i++;
            if (i == start) return 0;
            if (int.TryParse(conditionSentence.Substring(start, i - start),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec))
                return sec;
            return 0;
        }

        private static TextBlock Cell(string text, int col, Brush fg, double size)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontSize = size,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
                FontFamily = new FontFamily("Consolas"),
            };
            Grid.SetColumn(tb, col);
            return tb;
        }

        // ── Events ───────────────────────────────────────────────────────

        private void Back_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke(this, EventArgs.Empty);

        private void Priority_Click(object sender, RoutedEventArgs e)
            => PriorityRequested?.Invoke(this, EventArgs.Empty);

        private void ShowAllFields_Click(object sender, RoutedEventArgs e)
            => ClearFocusCore();

        private void PreviewChrome_Click(object sender, MouseButtonEventArgs e)
        {
            // Clear route 3: empty chrome = click that is NOT over a hit-region control.
            // Hit-test against region outlines (Tag starts with "hit:"), not source==Grid —
            // labels, dividers, and watermark are non-hit chrome and must clear focus.
            if (e.Handled)
                return;
            var src = e.OriginalSource as DependencyObject;
            while (src != null)
            {
                if (src is FrameworkElement fe
                    && fe.Tag is string tag
                    && tag.StartsWith("hit:", StringComparison.Ordinal))
                {
                    return; // landed on a hit region (should already be Handled)
                }
                if (ReferenceEquals(src, sender))
                    break;
                src = VisualTreeHelper.GetParent(src);
            }
            ClearFocusCore();
        }

        private void Root_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Clear route 4: Esc.
            if (e.Key == Key.Escape)
            {
                if (popupOverride.IsOpen)
                {
                    CloseOverrideForm();
                    e.Handled = true;
                    return;
                }
                if (popupRotation.IsOpen)
                {
                    popupRotation.IsOpen = false;
                    e.Handled = true;
                    return;
                }
                if (_focusedParamId.HasValue)
                {
                    ClearFocusCore();
                    e.Handled = true;
                }
            }
        }

        private void RotationMenu_Click(object sender, RoutedEventArgs e)
        {
            // D17: dialog primary for rotation editing.
            OpenRotationDialog();
        }

        private void OpenRotationDialog()
        {
            if (_model == null) return;
            _rotationWorkingOrder = new List<string>();
            _rotationDirty = false;
            var live = _host?.GetDisplayConfigV2();
            _rotationWasAbsent = live?.PageOrder == null;

            panelRotationIn.Children.Clear();
            panelRotationOut.Children.Clear();

            for (int i = 0; i < _model.RotationIn.Count; i++)
            {
                var item = _model.RotationIn[i];
                if (item == null) continue;
                _rotationWorkingOrder.Add(item.PageKey);
                panelRotationIn.Children.Add(RotationRow(item, inList: true, indexInList: i));
            }
            for (int i = 0; i < _model.RotationOut.Count; i++)
            {
                var item = _model.RotationOut[i];
                if (item == null) continue;
                panelRotationOut.Children.Add(RotationRow(item, inList: false, indexInList: i));
            }
            ConstrainRotationModal();
            popupRotation.IsOpen = true;
        }

        private void Root_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (popupOverride != null && popupOverride.IsOpen)
                ConstrainOverrideModal();
            if (popupRotation != null && popupRotation.IsOpen)
                ConstrainRotationModal();
        }

        private void ConstrainOverrideModal()
        {
            DisplayModalLayout.Constrain(
                this,
                popupOverride,
                chromeOverrideModal,
                fallbackHeight: 640);
        }

        private void ConstrainRotationModal()
        {
            DisplayModalLayout.Constrain(
                this,
                popupRotation,
                chromeRotationModal,
                fallbackHeight: 640);
        }

        private UIElement RotationRow(
            PagesFieldsRotationItemModel item, bool inList, int indexInList)
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // D17: reorder within the dialog via up/down (membership via click).
            var controls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            controls.Children.Add(new TextBlock
            {
                Text = DisplayCopy.GripGlyph,
                FontSize = 11,
                Foreground = MutedFg,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
            });
            if (inList)
            {
                string moveKey = item.PageKey;
                int idx = indexInList;
                controls.Children.Add(MakeTinyButton("↑", () =>
                {
                    if (_rotationWorkingOrder == null || idx <= 0) return;
                    string k = _rotationWorkingOrder[idx];
                    _rotationWorkingOrder.RemoveAt(idx);
                    _rotationWorkingOrder.Insert(idx - 1, k);
                    _rotationDirty = true;
                    RebuildRotationDialogFromWorking();
                }));
                controls.Children.Add(MakeTinyButton("↓", () =>
                {
                    if (_rotationWorkingOrder == null
                        || idx < 0
                        || idx >= _rotationWorkingOrder.Count - 1)
                        return;
                    string k = _rotationWorkingOrder[idx];
                    _rotationWorkingOrder.RemoveAt(idx);
                    _rotationWorkingOrder.Insert(idx + 1, k);
                    _rotationDirty = true;
                    RebuildRotationDialogFromWorking();
                }));
            }
            Grid.SetColumn(controls, 0);
            grid.Children.Add(controls);

            string stepText = item.Step.HasValue
                ? item.Step.Value.ToString(CultureInfo.InvariantCulture)
                : DisplayCopy.RotationStepAbsent;
            grid.Children.Add(Cell(stepText, 1, MutedFg, 12));

            var nameStack = new StackPanel();
            nameStack.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontSize = 12.5,
                Foreground = WhiteFg,
            });
            nameStack.Children.Add(new TextBlock
            {
                Text = item.WhyLine ?? string.Empty,
                FontSize = 11,
                Foreground = MutedFg,
            });
            Grid.SetColumn(nameStack, 2);
            grid.Children.Add(nameStack);

            var border = new Border
            {
                Child = grid,
                Padding = new Thickness(4),
                BorderBrush = inList ? Brushes.Transparent : DashedBorder,
                BorderThickness = inList ? new Thickness(0) : new Thickness(1),
                Background = inList ? CardBg : Brushes.Transparent,
                Cursor = Cursors.Hand,
            };

            // Click toggles membership in the working order (D17).
            string key = item.PageKey;
            border.MouseLeftButtonDown += (s, e) =>
            {
                if (e.Handled) return;
                if (_rotationWorkingOrder == null) return;
                if (inList)
                    _rotationWorkingOrder.Remove(key);
                else if (!_rotationWorkingOrder.Contains(key))
                    _rotationWorkingOrder.Add(key);
                _rotationDirty = true;
                RebuildRotationDialogFromWorking();
            };
            return border;
        }

        private void RebuildRotationDialogFromWorking()
        {
            if (_model == null || _rotationWorkingOrder == null) return;
            panelRotationIn.Children.Clear();
            panelRotationOut.Children.Clear();
            var inSet = new HashSet<string>(_rotationWorkingOrder, StringComparer.Ordinal);

            for (int i = 0; i < _rotationWorkingOrder.Count; i++)
            {
                string key = _rotationWorkingOrder[i];
                string name = key;
                for (int p = 0; p < _model.PageButtons.Count; p++)
                {
                    if (string.Equals(_model.PageButtons[p].Key, key, StringComparison.Ordinal))
                    {
                        name = _model.PageButtons[p].Name;
                        break;
                    }
                }
                var item = new PagesFieldsRotationItemModel(
                    key, name, i + 1, DisplayCopy.RotationWhyOnlyRoute, true);
                panelRotationIn.Children.Add(RotationRow(item, inList: true, indexInList: i));
            }
            for (int p = 0; p < _model.PageButtons.Count; p++)
            {
                var btn = _model.PageButtons[p];
                if (inSet.Contains(btn.Key)) continue;
                var item = new PagesFieldsRotationItemModel(
                    btn.Key, btn.Name, null,
                    DisplayCopy.RotationWhyArrivesViaEntrypoints, false);
                panelRotationOut.Children.Add(RotationRow(item, inList: false, indexInList: p));
            }
        }

        private void RotationSave_Click(object sender, RoutedEventArgs e)
        {
            // Unchanged open→save: leave pageOrder alone (absent stays absent).
            if (_rotationDirty)
                RotationSaveCore(_rotationWorkingOrder);
            popupRotation.IsOpen = false;
            _rotationWorkingOrder = null;
            _rotationDirty = false;
        }

        private void RotationCancel_Click(object sender, RoutedEventArgs e)
            => popupRotation.IsOpen = false;

        private void OverrideSave_Click(object sender, RoutedEventArgs e)
        {
            OverrideSaveCore();
            CloseOverrideForm();
        }

        private void OverrideDelete_Click(object sender, RoutedEventArgs e)
        {
            OverrideDeleteCore();
            CloseOverrideForm();
        }

        private void OverrideSplit_Click(object sender, RoutedEventArgs e)
        {
            if (SplitCurrentOverrideCore())
                CloseOverrideForm();
        }

        /// <summary>5g footer path: split this flagged override into a ChildRef satellite.</summary>
        internal bool SplitCurrentOverrideCore()
        {
            if (!CanSplitCurrentOverride())
                return false;
            ushort paramId = _ovParamId;
            string overrideId = _ovOverrideId;
            string homeRowId = _ovHomeRowId;
            ApplyEdit(session => session.SplitSatellite(
                homeRowId,
                new ChildRef
                {
                    Field = paramId.ToString(CultureInfo.InvariantCulture),
                    OverrideId = overrideId,
                }));
            return true;
        }

        private bool CanSplitCurrentOverride()
        {
            if (_host == null
                || _ovIsNew
                || string.IsNullOrEmpty(_ovOverrideId)
                || string.IsNullOrEmpty(_ovHomeRowId))
                return false;
            var config = _host.GetDisplayConfigV2();
            if (!FieldLadderMap.TryFindOverride(
                    config, _catalog, _ovParamId, _ovOverrideId, out var ov)
                || ov == null
                || !ov.ActsAsEntrypoint
                || ov.ActsAsEntrypointIgnored)
                return false;
            var rows = config?.Priority?.Rows;
            if (rows == null)
                return true;
            string field = _ovParamId.ToString(CultureInfo.InvariantCulture);
            for (int i = 0; i < rows.Count; i++)
            {
                var child = rows[i]?.ChildRef;
                if (child != null
                    && string.Equals(child.Field, field, StringComparison.Ordinal)
                    && string.Equals(child.OverrideId, _ovOverrideId, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private void OverrideCancel_Click(object sender, RoutedEventArgs e)
            => CloseOverrideForm();

        private void CloseOverrideForm()
        {
            popupOverride.IsOpen = false;
            txtFormLeafSep.Visibility = Visibility.Collapsed;
            txtFormLeaf.Visibility = Visibility.Collapsed;
        }

        private void PropertyRow_Click(object sender, MouseButtonEventArgs e)
        {
            // A-N15: PropertyPickerDialog — same seam as Priority 5f.
            if (TryPickProperty(txtOvSourcePath.Text, out string picked, out PropertyKind kind)
                && !string.IsNullOrEmpty(picked))
            {
                OverridePickerResultCore(picked, kind);
            }
            e.Handled = true;
        }

        private FieldOverride BuildOverrideFromForm()
        {
            bool writeValue = chkOvValue.IsChecked == true;
            bool writeSuffix = chkOvSuffix.IsChecked == true;
            FieldWrites writes = FieldWrites.Suffix;
            if (writeValue && writeSuffix) writes = FieldWrites.Both;
            else if (writeValue) writes = FieldWrites.Value;
            else if (writeSuffix) writes = FieldWrites.Suffix;

            string opText = cmbOvOperator.SelectedItem as string ?? DisplayCopy.OpBelow;
            ConditionOperator op = OperatorFromFormLabel(opText);

            double? value = null;
            if (op != ConditionOperator.IsTrue && op != ConditionOperator.IsFalse
                && double.TryParse(txtOvValue.Text, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double v))
                value = v;

            // Form authors only the members it shows. Lifetime is NOT on the form
            // (bring-up lives on the seat) — leave null on edit so clone-merge keeps it.
            // Enabled is not on the form — preserve prior (_ovEnabled).
            var ov = new FieldOverride
            {
                Id = _ovIsNew ? null : _ovOverrideId,
                Writes = writes,
                Content = new ContentObject
                {
                    Kind = ContentKind.Text,
                    Text = txtOvSuffixContent.Text ?? string.Empty,
                },
                Condition = new FanaBridge.Display.Schema2.Condition
                {
                    Source = new FanaBridge.Display.Schema2.ValueSource
                    {
                        Kind = _ovSourceKind == ValueSourceKind.Unknown
                            ? ValueSourceKind.SimHubProperty
                            : _ovSourceKind,
                        Name = txtOvSourcePath.Text?.Trim() ?? string.Empty,
                    },
                    Operator = op,
                    Value = value,
                },
                Lifetime = _ovIsNew
                    ? new Lifetime { Kind = LifetimeKind.WhileTrue }
                    : null,
                Enabled = _ovIsNew ? true : _ovEnabled,
                ActsAsEntrypoint = chkOvEntrypoint.IsChecked == true,
            };
            // Force AlignmentRaw so left is authored (clone-merge authorship signal).
            ov.AlignmentRaw = radAlignRight.IsChecked == true ? "right" : "left";
            return ov;
        }

        private PagesFieldsFieldSectionModel FindSection(ushort paramId)
        {
            if (_model == null) return null;
            for (int g = 0; g < _model.ScopeGroups.Count; g++)
            {
                var sections = _model.ScopeGroups[g].Sections;
                for (int s = 0; s < sections.Count; s++)
                {
                    if (sections[s].ParamId == paramId)
                        return sections[s];
                }
            }
            for (int s = 0; s < _model.FlatSections.Count; s++)
            {
                if (_model.FlatSections[s].ParamId == paramId)
                    return _model.FlatSections[s];
            }
            // When filtered, section is still in groups.
            return null;
        }

        private static PageRef PageRefFromKey(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;
            if (key.StartsWith("itm:", StringComparison.Ordinal))
            {
                return new PageRef
                {
                    Kind = PageRefKind.ItmPage,
                    CatalogPageId = key.Substring(4),
                };
            }
            if (key.StartsWith("hosted:", StringComparison.Ordinal))
            {
                return new PageRef
                {
                    Kind = PageRefKind.HostedPage,
                    Id = key.Substring(7),
                };
            }
            return null;
        }

        private void ApplyEdit(Func<DisplayConfigV2EditSession, DisplayConfigV2> mutate)
        {
            if (_host == null) return;

            var live = _host.GetDisplayConfigV2();
            var session = DisplayConfigV2EditSession.Open(live);
            mutate(session);
            var result = session.TryApply(_host);

            if (result.IsConflict)
            {
                bannerConflict.Visibility = Visibility.Visible;
                txtConflict.Text = result.Message ?? DisplayCopy.ConfigEditConflict;
                Poll(force: true);
                return;
            }

            bannerConflict.Visibility = Visibility.Collapsed;
            ConfigApplied?.Invoke(this, EventArgs.Empty);
            Poll(force: true);
        }

        private static void SetHyperlinkText(Hyperlink link, string text)
        {
            if (link == null) return;
            link.Inlines.Clear();
            link.Inlines.Add(new Run(text ?? string.Empty));
        }

        private static SolidColorBrush Freeze(SolidColorBrush b)
        {
            if (b.CanFreeze) b.Freeze();
            return b;
        }
    }

    internal static class PagesFieldsVisualExtensions
    {
        public static TextBlock WithColumn(this TextBlock tb, int col)
        {
            Grid.SetColumn(tb, col);
            return tb;
        }
    }
}
