using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FanaBridge.Display.Catalog;
using FanaBridge.Display.Host;
using FanaBridge.Display.Schema2;
using FanaBridge.Profiles;

namespace FanaBridge.UI.Display
{
    /// <summary>
    /// Pure Add-a-page (v2) projection — no WPF. Board 5h: plain door live, setup porch
    /// inert (D20 — manifest family does not exist; SpokeArrivingLater idiom).
    /// Writes go through <see cref="DisplayConfigV2EditSession"/> (never raw mutation).
    /// </summary>
    public sealed class DisplayAddPageV2Model
    {
        private static readonly IReadOnlyList<AddPageDoorCardModel> NoDoors =
            new ReadOnlyCollection<AddPageDoorCardModel>(Array.Empty<AddPageDoorCardModel>());

        private static readonly IReadOnlyList<AddPageItmChoiceModel> NoItmChoices =
            new ReadOnlyCollection<AddPageItmChoiceModel>(Array.Empty<AddPageItmChoiceModel>());

        /// <summary>
        /// Rebuild the Add-a-page projection. Null config yields a minimal empty frame.
        /// </summary>
        public static DisplayAddPageV2Model Project(
            DisplayConfigV2 config,
            DisplayResolutionSnapshotModel resolution,
            DisplayType displayType,
            WheelCatalog catalog = null)
        {
            resolution = resolution ?? DisplayResolutionSnapshotModel.Empty;
            bool isItm = displayType == DisplayType.Itm;
            string surfaceWord = isItm ? DisplayCopy.ItmDisplay : DisplayCopy.SegmentDisplay;
            string situation = resolution.InGame ? DisplayCopy.InGame : DisplayCopy.SituationIdle;

            // D20: setup porch ships inert — no setup type family under src/.
            string setupPorchNote = DisplayCopy.NoSetupsAvailable;
            string setupPorchTooltip = DisplayCopy.SpokeArrivingLater("Setups");
            bool setupPorchEnabled = false;

            var doors = new ReadOnlyCollection<AddPageDoorCardModel>(new[]
            {
                new AddPageDoorCardModel(
                    AddPageDoorKind.Page,
                    DisplayCopy.DoorAPage,
                    DisplayCopy.DoorAPageSub,
                    enabled: true,
                    disabledTooltip: null),
                new AddPageDoorCardModel(
                    AddPageDoorKind.Entrypoint,
                    DisplayCopy.DoorAnEntrypoint,
                    DisplayCopy.DoorAnEntrypointSub,
                    enabled: true,
                    disabledTooltip: null),
                new AddPageDoorCardModel(
                    AddPageDoorKind.Override,
                    DisplayCopy.DoorAnOverride,
                    DisplayCopy.DoorAnOverrideSub,
                    enabled: true,
                    disabledTooltip: null),
            });

            var itmChoices = BuildItmChoices(config, catalog);
            string itmPickerEmptyState = itmChoices.Count == 0
                ? (HasCatalogPages(catalog)
                    ? DisplayCopy.EveryCatalogPageAlreadyOnWheel
                    : DisplayCopy.NoCatalogPagesAvailable)
                : null;

            return new DisplayAddPageV2Model(
                surfaceWord: surfaceWord,
                situationCopy: situation,
                inGame: resolution.InGame,
                isConnected: resolution.IsConnected,
                isItmWheel: isItm,
                setupPorchEnabled: setupPorchEnabled,
                setupPorchNote: setupPorchNote,
                setupPorchTooltip: setupPorchTooltip,
                setupSearchPlaceholder: DisplayCopy.SearchSetups,
                setupColumnLabel: DisplayCopy.StartFromASetup,
                plainDoorLabel: DisplayCopy.OrAddOneThing,
                plainDoorNote: DisplayCopy.NothingCreatedUntilSave,
                doors: doors,
                itmChoices: itmChoices,
                itmPickerEmptyState: itmPickerEmptyState,
                pageAddedNote: DisplayCopy.PageAddedAtTopOfPriority);
        }

        private DisplayAddPageV2Model(
            string surfaceWord,
            string situationCopy,
            bool inGame,
            bool isConnected,
            bool isItmWheel,
            bool setupPorchEnabled,
            string setupPorchNote,
            string setupPorchTooltip,
            string setupSearchPlaceholder,
            string setupColumnLabel,
            string plainDoorLabel,
            string plainDoorNote,
            IReadOnlyList<AddPageDoorCardModel> doors,
            IReadOnlyList<AddPageItmChoiceModel> itmChoices,
            string itmPickerEmptyState,
            string pageAddedNote)
        {
            SurfaceWord = surfaceWord;
            SituationCopy = situationCopy;
            InGame = inGame;
            IsConnected = isConnected;
            IsItmWheel = isItmWheel;
            SetupPorchEnabled = setupPorchEnabled;
            SetupPorchNote = setupPorchNote;
            SetupPorchTooltip = setupPorchTooltip;
            SetupSearchPlaceholder = setupSearchPlaceholder;
            SetupColumnLabel = setupColumnLabel;
            PlainDoorLabel = plainDoorLabel;
            PlainDoorNote = plainDoorNote;
            Doors = doors ?? NoDoors;
            ItmChoices = itmChoices ?? NoItmChoices;
            ItmPickerEmptyState = itmPickerEmptyState;
            PageAddedNote = pageAddedNote;
        }

        public string SurfaceWord { get; }
        public string SituationCopy { get; }
        public bool InGame { get; }
        public bool IsConnected { get; }
        public bool IsItmWheel { get; }

        /// <summary>Always false this wave — D20 inert setup porch.</summary>
        public bool SetupPorchEnabled { get; }

        /// <summary>Ruled inert body copy; never an empty porch card.</summary>
        public string SetupPorchNote { get; }

        /// <summary><see cref="DisplayCopy.SpokeArrivingLater"/> for "Setups".</summary>
        public string SetupPorchTooltip { get; }

        public string SetupSearchPlaceholder { get; }
        public string SetupColumnLabel { get; }
        public string PlainDoorLabel { get; }
        public string PlainDoorNote { get; }
        public IReadOnlyList<AddPageDoorCardModel> Doors { get; }
        public IReadOnlyList<AddPageItmChoiceModel> ItmChoices { get; }
        public string ItmPickerEmptyState { get; }
        public string PageAddedNote { get; }

        private static bool HasCatalogPages(WheelCatalog catalog)
        {
            if (catalog?.Itm?.Pages == null)
                return false;
            for (int i = 0; i < catalog.Itm.Pages.Count; i++)
            {
                var page = catalog.Itm.Pages[i];
                if (page != null && !string.IsNullOrEmpty(page.Id))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Catalog ITM pages currently Removed. Catalog pages are present by default,
        /// so absent/non-removed pages are already placed and must not turn Add into a
        /// disclosed-as-nothing edit. Hosted creation does not use this list.
        /// </summary>
        private static IReadOnlyList<AddPageItmChoiceModel> BuildItmChoices(
            DisplayConfigV2 config, WheelCatalog catalog)
        {
            var list = new List<AddPageItmChoiceModel>();
            if (catalog?.Itm?.Pages == null)
                return NoItmChoices;

            for (int i = 0; i < catalog.Itm.Pages.Count; i++)
            {
                var cp = catalog.Itm.Pages[i];
                if (cp == null || string.IsNullOrEmpty(cp.Id))
                    continue;

                bool removed = IsItmRemoved(config, cp.Id);
                if (!removed)
                    continue;
                string name = ResolveItmName(config, cp);
                list.Add(new AddPageItmChoiceModel(
                    catalogPageId: cp.Id,
                    name: name,
                    badge: DisplayCopy.ItmPageBadge(cp.Index),
                    isRemoved: removed));
            }

            return list.Count == 0
                ? NoItmChoices
                : new ReadOnlyCollection<AddPageItmChoiceModel>(list);
        }

        private static bool IsItmRemoved(DisplayConfigV2 config, string catalogPageId)
        {
            if (config?.Pages == null || string.IsNullOrEmpty(catalogPageId))
                return false;
            for (int i = 0; i < config.Pages.Count; i++)
            {
                var pe = config.Pages[i];
                if (pe != null
                    && pe.Kind == PageEntryKind.ItmPage
                    && string.Equals(pe.CatalogPageId, catalogPageId, StringComparison.Ordinal)
                    && pe.Removed)
                    return true;
            }
            return false;
        }

        private static string ResolveItmName(DisplayConfigV2 config, CatalogPage cp)
        {
            if (config?.Pages != null)
            {
                for (int i = 0; i < config.Pages.Count; i++)
                {
                    var pe = config.Pages[i];
                    if (pe != null
                        && pe.Kind == PageEntryKind.ItmPage
                        && string.Equals(pe.CatalogPageId, cp.Id, StringComparison.Ordinal)
                        && !string.IsNullOrEmpty(pe.NameOverride))
                        return pe.NameOverride;
                }
            }
            return !string.IsNullOrEmpty(cp.Name) ? cp.Name : cp.Id;
        }
    }

    public enum AddPageDoorKind
    {
        Page,
        Entrypoint,
        Override,
    }

    public sealed class AddPageDoorCardModel
    {
        public AddPageDoorCardModel(
            AddPageDoorKind kind,
            string title,
            string subtitle,
            bool enabled,
            string disabledTooltip)
        {
            Kind = kind;
            Title = title ?? string.Empty;
            Subtitle = subtitle ?? string.Empty;
            Enabled = enabled;
            DisabledTooltip = disabledTooltip;
        }

        public AddPageDoorKind Kind { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public bool Enabled { get; }
        public string DisabledTooltip { get; }
    }

    public sealed class AddPageItmChoiceModel
    {
        public AddPageItmChoiceModel(
            string catalogPageId, string name, string badge, bool isRemoved)
        {
            CatalogPageId = catalogPageId ?? string.Empty;
            Name = name ?? string.Empty;
            Badge = badge ?? string.Empty;
            IsRemoved = isRemoved;
        }

        public string CatalogPageId { get; }
        public string Name { get; }
        public string Badge { get; }
        public bool IsRemoved { get; }
    }
}
