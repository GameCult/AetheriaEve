using GameCult.Eve.Surface;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaRuntimeHangarCommands
    {
        public const string SurfaceId = "aetheria.hangar";
        public const string SelectShip = "aetheria.hangar.select_ship";
        public const string SelectTerminus = "aetheria.hangar.select_mode.terminus";
        public const string SelectStarbridge = "aetheria.hangar.select_mode.starbridge";
        public const string SelectArena = "aetheria.hangar.select_mode.arena";
        public const string SelectVerse = "aetheria.hangar.select_verse";
        public const string EditLoadout = "aetheria.hangar.edit_loadout";
        public const string ShowOverview = "aetheria.hangar.show_overview";
        public const string EquipItem = "aetheria.hangar.loadout.equip";
        public const string RemoveItem = "aetheria.hangar.loadout.remove";
        public const string Launch = "aetheria.hangar.launch";
        public const string Continue = "aetheria.hangar.continue";
        public const string ExpectedProgressionVerseId = "expectedProgressionVerseId";
        public const string ExpectedProgressionSourceRevision = "expectedProgressionSourceRevision";
        public const string ExpectedProgressionAuthorityRuntimeId = "expectedProgressionAuthorityRuntimeId";
    }

    public static class AetheriaRuntimeHangarSurfaceBuilder
    {
        public static EveSurfaceDocument Build(
            AetheriaHangarState hangar,
            string selectedShipId,
            string selectedMode,
            string updatedAtUtc,
            long version = 1,
            AetheriaRuntimeLoadoutTemplateCommit? loadout = null,
            AetheriaRuntimeCatalogSnapshot? catalog = null,
            AetheriaProgressionSourceDocument? progressionSource = null,
            string activeView = AetheriaHangarViews.Overview,
            string progressionAuthorityRuntimeId = "")
        {
            if (hangar == null) throw new ArgumentNullException(nameof(hangar));
            var ships = hangar.Ships ?? Array.Empty<AetheriaHangarShip>();
            var selected = ships.FirstOrDefault(ship => string.Equals(ship.ShipId, selectedShipId, StringComparison.Ordinal))
                ?? ships.FirstOrDefault();
            var mode = AetheriaGameModes.IsKnown(selectedMode) ? selectedMode : AetheriaGameModes.Terminus;
            var canLaunch = selected != null && loadout != null && string.Equals(selected.Status, AetheriaHangarShipStatuses.Available, StringComparison.Ordinal);
            var canContinue = selected != null &&
                string.Equals(selected.Status, AetheriaHangarShipStatuses.Deployed, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(selected.ActiveDeploymentId) &&
                (hangar.Deployments ?? Array.Empty<AetheriaDeploymentReceipt>()).Any(deployment =>
                    deployment.Accepted &&
                    string.Equals(deployment.DeploymentId, selected.ActiveDeploymentId, StringComparison.Ordinal) &&
                    string.Equals(deployment.Mode, mode, StringComparison.Ordinal));
            var equipment = loadout?.RootEntity?.Equipment ?? Array.Empty<AetheriaRuntimeLoadoutItemSlotCommit>();
            var inventory = hangar.Inventory ?? Array.Empty<AetheriaHangarItemStack>();
            var loadoutView = string.Equals(activeView, AetheriaHangarViews.Loadout, StringComparison.Ordinal);
            progressionSource ??= new AetheriaProgressionSourceDocument
            {
                AvailableVerses = new[]
                {
                    new AetheriaProgressionVerseOption
                    {
                        VerseId = AetheriaProgressionSources.Local,
                        DisplayName = "Local"
                    }
                }
            };

            var root = Component(
                "aetheria.hangar.root",
                "surface",
                Props(
                    ("title", "HANGAR"),
                    ("selectedMode", mode),
                    ("activeView", AetheriaHangarViews.IsKnown(activeView) ? activeView : AetheriaHangarViews.Overview),
                    ("hangarRevision", hangar.Revision.ToString(CultureInfo.InvariantCulture)),
                    ("progressionVerseId", ProgressionVerseId(progressionSource)),
                    ("progressionSourceRevision", progressionSource.Revision.ToString(CultureInfo.InvariantCulture)),
                    ("progressionAuthorityRuntimeId", progressionAuthorityRuntimeId ?? "")),
                new[]
                {
                    Component("aetheria.hangar.launcher", "row", Props(), new[]
                    {
                        VerseSelect(progressionSource),
                        Button("aetheria.hangar.mode.terminus", "TERMINUS", AetheriaRuntimeHangarCommands.SelectTerminus, progressionSource, progressionAuthorityRuntimeId,
                            ("selected", (mode == AetheriaGameModes.Terminus).ToString().ToLowerInvariant())),
                        Button("aetheria.hangar.mode.starbridge", "STARBRIDGE", AetheriaRuntimeHangarCommands.SelectStarbridge, progressionSource, progressionAuthorityRuntimeId,
                            ("selected", (mode == AetheriaGameModes.Starbridge).ToString().ToLowerInvariant())),
                        Button("aetheria.hangar.mode.arena", "ARENA", AetheriaRuntimeHangarCommands.SelectArena, progressionSource, progressionAuthorityRuntimeId,
                            ("selected", (mode == AetheriaGameModes.Arena).ToString().ToLowerInvariant())),
                        Button("aetheria.hangar.launch", "LAUNCH", AetheriaRuntimeHangarCommands.Launch, progressionSource, progressionAuthorityRuntimeId,
                            ("disabled", (!canLaunch).ToString().ToLowerInvariant()),
                            ("shipId", selected?.ShipId ?? ""),
                            ("mode", mode),
                            ("expectedHangarRevision", hangar.Revision.ToString(CultureInfo.InvariantCulture))),
                        Button("aetheria.hangar.continue", "CONTINUE", AetheriaRuntimeHangarCommands.Continue, progressionSource, progressionAuthorityRuntimeId,
                            ("disabled", (!canContinue).ToString().ToLowerInvariant()),
                            ("shipId", selected?.ShipId ?? ""),
                            ("deploymentId", selected?.ActiveDeploymentId ?? ""))
                    }, Layout(("direction", "horizontal"), ("justifyContent", "flex-end"), ("minHeight", "44"), ("padding", "6"))),
                    Component("aetheria.hangar.body", "partition", Props(("split", "x")), new[]
                    {
                        Component("aetheria.hangar.left", "partition", Props(("split", "y")), new[]
                        {
                            Panel("aetheria.hangar.ship_summary", "SHIP", selected == null
                                ? new[] { Text("aetheria.hangar.ship.none", "No ship selected") }
                                : new[]
                                {
                                    Metric("aetheria.hangar.ship.id", "Ship", selected.ShipId),
                                    Metric("aetheria.hangar.ship.hull", "Hull", selected.HullItemKey),
                                    Metric("aetheria.hangar.ship.status", "Status", selected.Status),
                                    Metric("aetheria.hangar.ship.loadout", "Loadout", selected.LoadoutTemplateKey)
                                }),
                            Panel("aetheria.hangar.inventory", "HANGAR INVENTORY", new[]
                            {
                                Component("aetheria.hangar.inventory.scroll", "scroll", Props(), new[]
                                {
                                    HangarInventoryGrid(selected, inventory, hangar.Revision, progressionSource, progressionAuthorityRuntimeId, catalog)
                                }, Layout(("flexGrow", "1"), ("overflowY", "auto")))
                            })
                        }, Layout(("minWidth", "260"), ("maxWidth", "360"), ("height", "100%"), ("overflow", "hidden"))),
                        Component("aetheria.hangar.center", "partition", Props(("split", "y")),
                            loadoutView
                            ? new[]
                            {
                                Panel("aetheria.hangar.loadout", "LOADOUT CONFIGURATION", new[]
                                {
                                    Text("aetheria.hangar.loadout.help", "Drag equipment between the Hangar inventory and valid ship cells."),
                                    Component("aetheria.hangar.loadout.scroll", "scroll", Props(), new[]
                                    {
                                        LoadoutGrid(selected, equipment, hangar.Revision, progressionSource, progressionAuthorityRuntimeId, catalog)
                                    }, Layout(("flexGrow", "1"), ("overflowY", "auto")))
                                })
                            }
                            : new[]
                            {
                                Panel("aetheria.hangar.preview", "SHIP PREVIEW", new[]
                                {
                                    Component("aetheria.hangar.world", "world.scene3d", Props(
                                        ("statePointerId", AetheriaRuntimeVerseRecordKeys.DaemonFrameLatest.ToString()),
                                        ("entityViewPointerId", AetheriaRuntimeVerseRecordKeys.EveEntitySoaViewLatest.ToString()),
                                        ("entityViewSchema", GameCult.Eve.Surface.EveEntitySoaViewDocument.SchemaId),
                                        ("entityBodyId", AetheriaRuntimeDaemonSoaFramePublisher.BodyId),
                                        ("zoneRenderPointerId", AetheriaRuntimeVerseRecordKeys.ZoneRenderLatest.ToString()),
                                        ("zoneRenderSchema", AetheriaRuntimeDaemonSchemas.ZoneRender),
                                        ("assetManifest", AetheriaRuntimeVerseRecordKeys.EveAssetCatalog.ToString()),
                                        ("cameraRig", "hangar-static"),
                                        ("viewId", "aetheria.hangar")), Array.Empty<EveSurfaceComponent>()),
                                    Component("aetheria.hangar.preview.slot", "asset.preview", Props(
                                        ("assetRole", "ship.preview"),
                                        ("subjectKey", selected?.ShipId ?? "")), Array.Empty<EveSurfaceComponent>())
                                }),
                                Panel("aetheria.hangar.loadout", "LOADOUT", new[]
                                {
                                    Component("aetheria.hangar.loadout.scroll", "scroll", Props(), new[]
                                    {
                                        LoadoutGrid(selected, equipment, hangar.Revision, progressionSource, progressionAuthorityRuntimeId, catalog)
                                    }, Layout(("flexGrow", "1"), ("overflowY", "auto")))
                                })
                            },
                            Layout(("minWidth", "480"), ("flexGrow", "1"), ("height", "100%"), ("overflow", "hidden"))),
                        Component("aetheria.hangar.right", "partition", Props(("split", "y")), new[]
                        {
                            Panel("aetheria.hangar.fit", "FIT SUMMARY", new[]
                            {
                                Metric("aetheria.hangar.fit.hull", "Hull", selected?.HullItemKey ?? "-"),
                                Metric("aetheria.hangar.fit.template", "Template", selected?.LoadoutTemplateKey ?? "-"),
                                Metric(
                                    "aetheria.hangar.fit.policy",
                                    "Authority",
                                    string.IsNullOrWhiteSpace(AetheriaModePolicies.ForMode(mode))
                                        ? "Not installed"
                                        : AetheriaModePolicies.ForMode(mode)),
                                Button("aetheria.hangar.fit.edit", loadoutView ? "DONE" : "EDIT LOADOUT",
                                    loadoutView ? AetheriaRuntimeHangarCommands.ShowOverview : AetheriaRuntimeHangarCommands.EditLoadout,
                                    progressionSource,
                                    progressionAuthorityRuntimeId,
                                    ("targetSurfaceId", AetheriaRuntimeHangarCommands.SurfaceId))
                            })
                        }, Layout(("minWidth", "280"), ("maxWidth", "360"), ("height", "100%")))
                    }, Layout(("flexGrow", "1"), ("minHeight", "420"), ("overflow", "hidden"))),
                    Component("aetheria.hangar.bays", "row", Props(("label", "OWNED SHIPS")),
                        ships.Select(ship => Button(
                            "aetheria.hangar.bay." + StableToken(ship.ShipId),
                            ship.ShipId,
                            AetheriaRuntimeHangarCommands.SelectShip,
                            progressionSource,
                            progressionAuthorityRuntimeId,
                            ("shipId", ship.ShipId),
                            ("selected", string.Equals(ship.ShipId, selected?.ShipId, StringComparison.Ordinal) ? "true" : "false"),
                            ("status", ship.Status))).ToArray(),
                        Layout(("direction", "horizontal"), ("minHeight", "92"), ("overflow", "hidden"), ("padding", "6")))
                },
                Layout(
                    ("direction", "vertical"),
                    ("height", "100%"),
                    ("minWidth", "1024"),
                    ("minHeight", "640"),
                    ("overflow", "hidden")),
                Style(("background", "#070b0d"), ("color", "#d8eef2")));

            return new EveSurfaceDocument(
                "aetheria",
                "game.hangar",
                "Aetheria Hangar",
                version,
                updatedAtUtc ?? "",
                new EveSurfaceTree(AetheriaRuntimeHangarCommands.SurfaceId, root, Array.Empty<EveStyleToken>()),
                new[]
                {
                    Command(AetheriaRuntimeHangarCommands.SelectShip, "Select Ship"),
                    Command(AetheriaRuntimeHangarCommands.SelectTerminus, "Select Terminus"),
                    Command(AetheriaRuntimeHangarCommands.SelectStarbridge, "Select Starbridge"),
                    Command(AetheriaRuntimeHangarCommands.SelectArena, "Select Arena"),
                    Command(AetheriaRuntimeHangarCommands.SelectVerse, "Select Verse"),
                    Command(AetheriaRuntimeHangarCommands.EditLoadout, "Edit Loadout"),
                    Command(AetheriaRuntimeHangarCommands.ShowOverview, "Show Hangar Overview"),
                    Command(AetheriaRuntimeHangarCommands.EquipItem, "Equip Item"),
                    Command(AetheriaRuntimeHangarCommands.RemoveItem, "Remove Item"),
                    Command(AetheriaRuntimeHangarCommands.Launch, "Launch"),
                    Command(AetheriaRuntimeHangarCommands.Continue, "Continue")
                });
        }

        private static string ItemName(AetheriaRuntimeCatalogSnapshot? catalog, string? itemKey) =>
            catalog?.FindItem(itemKey ?? "")?.Name ?? (string.IsNullOrWhiteSpace(itemKey) ? "Unknown" : itemKey);

        private static EveSurfaceComponent LoadoutGrid(
            AetheriaHangarShip? ship,
            IReadOnlyList<AetheriaRuntimeLoadoutItemSlotCommit> equipment,
            long hangarRevision,
            AetheriaProgressionSourceDocument progressionSource,
            string progressionAuthorityRuntimeId,
            AetheriaRuntimeCatalogSnapshot? catalog)
        {
            var hull = catalog?.FindItem(ship?.HullItemKey ?? "");
            var children = (equipment ?? Array.Empty<AetheriaRuntimeLoadoutItemSlotCommit>())
                .Select((slot, index) => InventoryItem(
                    "aetheria.hangar.loadout.item." + index.ToString(CultureInfo.InvariantCulture),
                    slot.Item?.ItemKey ?? "",
                    Math.Max(1, slot.Item?.Quantity ?? 1),
                    AetheriaRuntimeRefitSourceKinds.Equipment,
                    ship?.ShipId ?? "",
                    index,
                    slot.X,
                    slot.Y,
                    slot.Rotation,
                    catalog))
                .ToArray();
            var validCells = (hull?.InteriorShapeCells ?? Array.Empty<AetheriaRuntimeShapeCell>())
                .Select(cell => (X: cell.X, Y: cell.Y))
                .Concat((hull?.Hardpoints ?? Array.Empty<AetheriaRuntimeHardpoint>())
                    .SelectMany(hardpoint => (hardpoint.ShapeCells ?? Array.Empty<AetheriaRuntimeShapeCell>())
                        .Select(cell => (X: hardpoint.PositionX + cell.X, Y: hardpoint.PositionY + cell.Y))))
                .Distinct()
                .OrderBy(cell => cell.Y)
                .ThenBy(cell => cell.X);
            return Component(
                "aetheria.hangar.loadout.grid",
                EveInventoryInteraction.GridKind,
                Props(new[]
                    {
                        ("title", "Installed Equipment"),
                        ("targetKind", AetheriaRuntimeRefitSourceKinds.Equipment),
                        ("targetEntityKey", ship?.ShipId ?? ""),
                        ("columns", Math.Max(1, hull?.InteriorShapeWidth ?? 1).ToString(CultureInfo.InvariantCulture)),
                        ("rows", Math.Max(1, hull?.InteriorShapeHeight ?? 1).ToString(CultureInfo.InvariantCulture)),
                        ("cellSize", "36"),
                        ("cellGap", "2"),
                        ("validCells", Cells(validCells)),
                        ("dropCommand.hangar", AetheriaRuntimeHangarCommands.EquipItem),
                        ("payload.shipId", ship?.ShipId ?? ""),
                        ("payload.expectedHangarRevision", hangarRevision.ToString(CultureInfo.InvariantCulture))
                    }
                    .Concat(TargetPayloadProps(progressionSource, progressionAuthorityRuntimeId))
                    .ToArray()),
                children);
        }

        private static EveSurfaceComponent HangarInventoryGrid(
            AetheriaHangarShip? ship,
            IReadOnlyList<AetheriaHangarItemStack> inventory,
            long hangarRevision,
            AetheriaProgressionSourceDocument progressionSource,
            string progressionAuthorityRuntimeId,
            AetheriaRuntimeCatalogSnapshot? catalog)
        {
            const int minimumColumns = 8;
            var children = new List<EveSurfaceComponent>();
            var y = 0;
            var columns = minimumColumns;
            var stacks = inventory ?? Array.Empty<AetheriaHangarItemStack>();
            for (var index = 0; index < stacks.Count; index++)
            {
                var stack = stacks[index];
                var item = catalog?.FindItem(stack.ItemKey);
                columns = Math.Max(columns, Math.Max(1, item?.ShapeWidth ?? 1));
                children.Add(InventoryItem(
                    "aetheria.hangar.inventory.item." + index.ToString(CultureInfo.InvariantCulture),
                    stack.ItemKey,
                    stack.Quantity,
                    "hangar",
                    "hangar",
                    index,
                    0,
                    y,
                    "None",
                    catalog));
                y += Math.Max(1, item?.ShapeHeight ?? 1);
            }

            return Component(
                "aetheria.hangar.inventory.grid",
                EveInventoryInteraction.GridKind,
                Props(new[]
                    {
                        ("title", "Stored Equipment"),
                        ("targetKind", "hangar"),
                        ("targetEntityKey", "hangar"),
                        ("columns", columns.ToString(CultureInfo.InvariantCulture)),
                        ("rows", Math.Max(1, y).ToString(CultureInfo.InvariantCulture)),
                        ("cellSize", "36"),
                        ("cellGap", "2"),
                        ("dropCommand.equipment", AetheriaRuntimeHangarCommands.RemoveItem),
                        ("payload.shipId", ship?.ShipId ?? ""),
                        ("payload.expectedHangarRevision", hangarRevision.ToString(CultureInfo.InvariantCulture))
                    }
                    .Concat(TargetPayloadProps(progressionSource, progressionAuthorityRuntimeId))
                    .ToArray()),
                children);
        }

        private static EveSurfaceComponent InventoryItem(
            string id,
            string itemKey,
            long quantity,
            string sourceKind,
            string sourceEntityKey,
            int sourceIndex,
            int x,
            int y,
            string? rotation,
            AetheriaRuntimeCatalogSnapshot? catalog)
        {
            var item = catalog?.FindItem(itemKey);
            var normalizedRotation = string.IsNullOrWhiteSpace(rotation) ? "None" : rotation!;
            var cells = item == null
                ? new[] { (X: 0, Y: 0) }
                : AetheriaRuntimeEquipmentGridGeometry.RotatedCells(
                    item,
                    AetheriaRuntimeEquipmentGridGeometry.ParseRotation(normalizedRotation)).ToArray();
            return Component(
                id,
                EveInventoryInteraction.ItemKind,
                Props(
                    ("label", ItemName(catalog, itemKey)),
                    ("itemKey", itemKey),
                    ("quantity", Math.Max(1L, quantity).ToString(CultureInfo.InvariantCulture)),
                    ("sourceKind", sourceKind),
                    ("sourceEntityKey", sourceEntityKey),
                    ("sourceIndex", sourceIndex.ToString(CultureInfo.InvariantCulture)),
                    ("x", x.ToString(CultureInfo.InvariantCulture)),
                    ("y", y.ToString(CultureInfo.InvariantCulture)),
                    ("rotation", normalizedRotation),
                    ("shapeWidth", Math.Max(1, item?.ShapeWidth ?? 1).ToString(CultureInfo.InvariantCulture)),
                    ("shapeHeight", Math.Max(1, item?.ShapeHeight ?? 1).ToString(CultureInfo.InvariantCulture)),
                    ("shapeCells", Cells(cells)),
                    ("draggable", "true")),
                Array.Empty<EveSurfaceComponent>());
        }

        private static string Cells(IEnumerable<(int X, int Y)> cells) =>
            string.Join(";", cells.Select(cell =>
                cell.X.ToString(CultureInfo.InvariantCulture) + "," + cell.Y.ToString(CultureInfo.InvariantCulture)));

        private static EveSurfaceComponent Panel(string id, string title, IReadOnlyList<EveSurfaceComponent> children) =>
            Component(id, "pane", Props(("title", title)), children, Layout(("flexGrow", "1"), ("minHeight", "120"), ("padding", "12"), ("overflow", "hidden")), Style(("background", "#11181c"), ("borderWidth", "1"), ("borderColor", "#60737a")));

        private static EveSurfaceComponent Metric(string id, string label, string value) =>
            Component(id, "metric", Props(("label", label), ("value", value)), Array.Empty<EveSurfaceComponent>());

        private static EveSurfaceComponent Text(string id, string value) =>
            Component(id, "text", Props(("value", value)), Array.Empty<EveSurfaceComponent>());

        private static EveSurfaceComponent Button(
            string id,
            string label,
            string command,
            AetheriaProgressionSourceDocument progressionSource,
            string progressionAuthorityRuntimeId,
            params (string Key, string Value)[] extra) =>
            Component(
                id,
                "control.button",
                Props(new[] { ("label", label), ("command", command) }
                    .Concat(extra)
                    .Concat(TargetPayloadProps(progressionSource, progressionAuthorityRuntimeId))
                    .ToArray()),
                Array.Empty<EveSurfaceComponent>());

        private static string ProgressionVerseId(AetheriaProgressionSourceDocument source) =>
            string.IsNullOrWhiteSpace(source.SelectedVerseId)
                ? AetheriaProgressionSources.Local
                : source.SelectedVerseId;

        private static (string Key, string Value)[] TargetPayloadProps(
            AetheriaProgressionSourceDocument source,
            string progressionAuthorityRuntimeId) =>
            new[]
            {
                ("payload." + AetheriaRuntimeHangarCommands.ExpectedProgressionVerseId, ProgressionVerseId(source)),
                ("payload." + AetheriaRuntimeHangarCommands.ExpectedProgressionSourceRevision,
                    source.Revision.ToString(CultureInfo.InvariantCulture)),
                ("payload." + AetheriaRuntimeHangarCommands.ExpectedProgressionAuthorityRuntimeId,
                    progressionAuthorityRuntimeId ?? "")
            };

        private static EveSurfaceComponent VerseSelect(AetheriaProgressionSourceDocument source)
        {
            var options = (source.AvailableVerses ?? Array.Empty<AetheriaProgressionVerseOption>())
                .Where(option => !string.IsNullOrWhiteSpace(option.VerseId))
                .GroupBy(option => option.VerseId, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(option => string.Equals(option.VerseId, AetheriaProgressionSources.Local, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(option => Component(
                    "aetheria.hangar.verse.option." + StableToken(option.VerseId),
                    "control.option",
                    Props(
                        ("label", string.IsNullOrWhiteSpace(option.DisplayName) ? option.VerseId : option.DisplayName),
                        ("value", option.VerseId)),
                    Array.Empty<EveSurfaceComponent>()))
                .ToArray();

            return Component(
                "aetheria.hangar.verse",
                "control.select",
                Props(
                    ("label", "VERSE"),
                    ("value", string.IsNullOrWhiteSpace(source.SelectedVerseId) ? AetheriaProgressionSources.Local : source.SelectedVerseId),
                    ("command", AetheriaRuntimeHangarCommands.SelectVerse),
                    ("status", source.Status ?? ""),
                    ("diagnostic", source.Diagnostic ?? "")),
                options);
        }

        private static EveCommandTemplate Command(string command, string label) =>
            AetheriaRuntimeSurfaceDocuments.Command(command, label, "cultmesh");

        private static EveSurfaceComponent Component(
            string id,
            string kind,
            IReadOnlyDictionary<string, string> props,
            IReadOnlyList<EveSurfaceComponent> children,
            IReadOnlyDictionary<string, string>? layout = null,
            IReadOnlyDictionary<string, string>? style = null) =>
            new EveSurfaceComponent(id, kind, props, children, Array.Empty<GameCult.Mesh.CultMeshStateBindingDescriptor>(), Array.Empty<EveEmbeddedDocumentSlot>(), layout, style);

        private static IReadOnlyDictionary<string, string> Props(params (string Key, string Value)[] values) =>
            values.ToDictionary(value => value.Key, value => value.Value ?? "", StringComparer.Ordinal);

        private static IReadOnlyDictionary<string, string> Layout(params (string Key, string Value)[] values) => Props(values);
        private static IReadOnlyDictionary<string, string> Style(params (string Key, string Value)[] values) => Props(values);

        private static string StableToken(string value) =>
            new string((value ?? "").Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '.').ToArray()).Trim('.');
    }
}
