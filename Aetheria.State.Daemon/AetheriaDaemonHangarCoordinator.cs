using Aetheria.State;
using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;
using GameCult.Caching;

public static class AetheriaDaemonHangarCoordinator
{
    public const string LocalPlayerKey = "player:local";
    public const string StarterShipId = "ship:local:vanguard";
    public const string StarterLoadoutName = "Vanguard One";

    public static async Task EnsureAsync(
        AetheriaStateNode node,
        AetheriaRuntimeCatalogSnapshot catalog,
        string now)
    {
        await node.CommitAsync(async () =>
        {
            var existing = await node.MutableDocument<AetheriaHangarState>(AetheriaStateNode.HangarKey)
                .ReadAsync().ConfigureAwait(false);
            if (existing != null)
            {
                await EnsureDraftCoreAsync(node, existing, now).ConfigureAwait(false);
                return;
            }

        var factionKey = (catalog.Corporations ?? [])
            .Select(value => value.CorporationKey)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? throw new InvalidDataException("Hangar creation requires one typed corporation.");
        var generator = new AetheriaDaemonLoadoutGenerator(
            catalog,
            AetheriaDaemonZoneGenerator.GenerationSeed,
            0,
            new Dictionary<string, int>(StringComparer.Ordinal) { [factionKey] = 0 },
            new Dictionary<int, IReadOnlyList<int>> { [0] = [] },
            isPrelude: true);
        var generated = generator.Build("ship", factionKey);
        var loadoutCommit = new AetheriaRuntimeLoadoutTemplateCommit
        {
            Name = StarterLoadoutName,
            OwnerPlayerKey = LocalPlayerKey,
            RootEntity = ToRuntimeLoadout(generated, factionKey)
        };
        var loadout = AetheriaRuntimeStateMapper.ToLoadoutTemplate(loadoutCommit, now);
        var loadoutKey = AetheriaRuntimeStateMapper.LoadoutKey(loadout.Name);
        var installed = generated.Equipment
            .Concat(generated.CargoBays)
            .Concat(generated.DockingBays)
            .Select(value => value.ItemKey)
            .ToHashSet(StringComparer.Ordinal);
        var inventory = catalog.EquipmentItems
            .Where(value => !string.IsNullOrWhiteSpace(value.ItemKey) && !installed.Contains(value.ItemKey))
            .OrderBy(value => value.Price)
            .ThenBy(value => value.ItemKey, StringComparer.Ordinal)
            .Take(24)
            .Select(value => new AetheriaHangarItemStack { ItemKey = value.ItemKey, Quantity = 1 })
            .ToArray();

            await node.MutableDocument<AetheriaLoadoutTemplate>(loadoutKey).ReplaceAsync(loadout).ConfigureAwait(false);
            await node.MutableDocument<AetheriaHangarState>(AetheriaStateNode.HangarKey).ReplaceAsync(new AetheriaHangarState
            {
            HangarId = "local",
            PlayerKey = LocalPlayerKey,
            Revision = 1,
            Ships =
            [
                new AetheriaHangarShip
                {
                    ShipId = StarterShipId,
                    HullItemKey = generated.HullItemKey,
                    LoadoutTemplateKey = loadoutKey.ToString(),
                    Status = AetheriaHangarShipStatuses.Available
                }
            ],
            Inventory = inventory,
            LoadoutTemplateKeys = [loadoutKey.ToString()],
            UpdatedAtUtc = now
            }).ConfigureAwait(false);
            await node.MutableDocument<AetheriaHangarDraftState>(AetheriaStateNode.HangarDraftKey).ReplaceAsync(new AetheriaHangarDraftState
            {
            PlayerKey = LocalPlayerKey,
            SelectedShipId = StarterShipId,
            SelectedMode = AetheriaGameModes.Terminus,
            ActiveView = AetheriaHangarViews.Overview,
            Revision = 1,
            UpdatedAtUtc = now
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    public static async Task<AetheriaHangarDraftState> EnsureDraftAsync(
        AetheriaStateNode node,
        AetheriaHangarState hangar,
        string now)
    {
        return await node.CommitAsync(
            () => EnsureDraftCoreAsync(node, hangar, now)).ConfigureAwait(false);
    }

    private static async Task<AetheriaHangarDraftState> EnsureDraftCoreAsync(
        AetheriaStateNode node,
        AetheriaHangarState hangar,
        string now)
    {
        var pointer = node.MutableDocument<AetheriaHangarDraftState>(AetheriaStateNode.HangarDraftKey);
        var existing = await pointer.ReadAsync().ConfigureAwait(false);
        var availableShipIds = (hangar.Ships ?? []).Select(ship => ship.ShipId).ToHashSet(StringComparer.Ordinal);
        var selectedShipId = existing != null && availableShipIds.Contains(existing.SelectedShipId)
            ? existing.SelectedShipId
            : (hangar.Ships ?? []).FirstOrDefault()?.ShipId ?? "";
        var selectedMode = existing != null && AetheriaGameModes.IsKnown(existing.SelectedMode)
            ? existing.SelectedMode
            : AetheriaGameModes.Terminus;
        var activeView = existing != null && AetheriaHangarViews.IsKnown(existing.ActiveView)
            ? existing.ActiveView
            : AetheriaHangarViews.Overview;
        if (existing != null &&
            string.Equals(existing.PlayerKey, hangar.PlayerKey, StringComparison.Ordinal) &&
            string.Equals(existing.SelectedShipId, selectedShipId, StringComparison.Ordinal) &&
            string.Equals(existing.SelectedMode, selectedMode, StringComparison.Ordinal) &&
            string.Equals(existing.ActiveView, activeView, StringComparison.Ordinal))
            return existing;

        var next = new AetheriaHangarDraftState
        {
            PlayerKey = hangar.PlayerKey,
            SelectedShipId = selectedShipId,
            SelectedMode = selectedMode,
            ActiveView = activeView,
            Revision = Math.Max(0, existing?.Revision ?? 0) + 1,
            UpdatedAtUtc = now
        };
        await pointer.ReplaceAsync(next).ConfigureAwait(false);
        return next;
    }

    public static Task<AetheriaHangarDraftState> SelectShipAsync(
        AetheriaStateNode node,
        string shipId,
        string now) => MutateDraftAsync(node, now, (draft, hangar) =>
    {
        if (!(hangar.Ships ?? []).Any(ship => string.Equals(ship.ShipId, shipId, StringComparison.Ordinal)))
            throw new InvalidOperationException("Selected ship is not owned by this Hangar.");
        draft.SelectedShipId = shipId;
    });

    public static Task<AetheriaHangarDraftState> SelectModeAsync(
        AetheriaStateNode node,
        string mode,
        string now) => MutateDraftAsync(node, now, (draft, _) =>
    {
        if (!AetheriaGameModes.IsKnown(mode))
            throw new InvalidOperationException("Selected game mode is unknown.");
        draft.SelectedMode = mode;
    });

    public static Task<AetheriaHangarDraftState> SelectViewAsync(
        AetheriaStateNode node,
        string view,
        string now) => MutateDraftAsync(node, now, (draft, _) =>
    {
        if (!AetheriaHangarViews.IsKnown(view))
            throw new InvalidOperationException("Selected Hangar view is unknown.");
        draft.ActiveView = view;
    });

    public static async Task<AetheriaDeploymentReceipt> LaunchAsync(
        AetheriaStateNode node,
        AetheriaRuntimeCatalogSnapshot catalog,
        string requestId,
        string sessionId,
        string verseId,
        string hostRuntimeId,
        string controllerRuntimeId,
        long expectedRevision,
        string now)
    {
        return await node.CommitAsync(async () =>
        {
            var hangar = await node.MutableDocument<AetheriaHangarState>(AetheriaStateNode.HangarKey)
                .ReadAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("The canonical Hangar document does not exist.");
            var draft = await EnsureDraftCoreAsync(node, hangar, now).ConfigureAwait(false);
            var shipId = draft.SelectedShipId;
            var ship = (hangar.Ships ?? []).SingleOrDefault(value => string.Equals(value.ShipId, shipId, StringComparison.Ordinal));
            var request = new AetheriaDeploymentRequest
            {
                RequestId = requestId,
                PlayerKey = hangar.PlayerKey,
                Mode = draft.SelectedMode,
                ShipId = shipId,
                LoadoutTemplateKey = ship?.LoadoutTemplateKey ?? "",
                ExpectedHangarRevision = expectedRevision,
                ModePolicyId = AetheriaModePolicies.ForMode(draft.SelectedMode)
            };
            var loadout = string.IsNullOrWhiteSpace(request.LoadoutTemplateKey)
                ? null
                : await node.MutableDocument<AetheriaLoadoutTemplate>(new CultRecordKey(request.LoadoutTemplateKey))
                    .ReadAsync().ConfigureAwait(false);
            var admission = AetheriaHangar.Plan(hangar, request, loadout, now);
            var receipt = admission.Receipt;
            if (!receipt.Accepted)
                return receipt;

            await AetheriaDaemonZoneGenerator.WritePlayableRunAsync(
                node,
                catalog,
                now,
                AetheriaDaemonTerminusScenarios.Standard,
                receipt,
                flush: false).ConfigureAwait(false);
            await node.MutableDocument<AetheriaHangarState>(AetheriaStateNode.HangarKey)
                .ReplaceAsync(admission.Hangar).ConfigureAwait(false);
            var run = await node.MutableDocument<AetheriaRunState>(new CultRecordKey(receipt.RunRecordKey))
                .ReadAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Generated deployment run is missing before atomic activation.");
            await ActivateDeploymentAsync(
                node,
                receipt,
                run,
                sessionId,
                requestId,
                verseId,
                hostRuntimeId,
                controllerRuntimeId,
                now).ConfigureAwait(false);
            return receipt;
        }).ConfigureAwait(false);
    }

    public static async Task<bool> CanContinueAsync(
        AetheriaStateNode node,
        string deploymentId)
        => await FindContinuationAsync(node, deploymentId).ConfigureAwait(false) != null;

    public static async Task<AetheriaDeploymentReceipt?> ContinueAsync(
        AetheriaStateNode node,
        string deploymentId,
        string sessionId,
        string commandId,
        string verseId,
        string hostRuntimeId,
        string controllerRuntimeId,
        string now)
    {
        return await node.CommitAsync(async () =>
        {
            var deployment = await FindContinuationAsync(node, deploymentId).ConfigureAwait(false);
            if (deployment == null) return null;
            var run = await node.MutableDocument<AetheriaRunState>(new CultRecordKey(deployment.RunRecordKey))
                .ReadAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Continued deployment has no canonical run checkpoint.");
            await ActivateDeploymentAsync(
                node,
                deployment,
                run,
                sessionId,
                commandId,
                verseId,
                hostRuntimeId,
                controllerRuntimeId,
                now).ConfigureAwait(false);
            return deployment;
        }).ConfigureAwait(false);
    }

    private static async Task ActivateDeploymentAsync(
        AetheriaStateNode node,
        AetheriaDeploymentReceipt deployment,
        AetheriaRunState run,
        string sessionId,
        string commandId,
        string verseId,
        string hostRuntimeId,
        string controllerRuntimeId,
        string now)
    {
        var policy = string.Equals(deployment.Mode, AetheriaGameModes.Arena, StringComparison.Ordinal)
            ? AetheriaRuntimeVerseAuthorityPolicyDocument.ArenaServerAuthoritative(verseId, hostRuntimeId)
            : AetheriaRuntimeVerseAuthorityPolicyDocument.TrustedCoop(verseId, hostRuntimeId);
        if (string.Equals(deployment.Mode, AetheriaGameModes.Arena, StringComparison.Ordinal) &&
            !string.Equals(deployment.ModePolicyId, policy.PolicyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Arena deployment does not own the installed server-authority policy.");
        }

        var settingsPointer = node.MutableDocument<AetheriaPlayerSettings>(AetheriaStateNode.PlayerSettingsKey);
        var settings = await settingsPointer.ReadAsync().ConfigureAwait(false) ?? new AetheriaPlayerSettings();
        settings.ActiveRunKey = deployment.RunRecordKey;
        settings.LastUpdatedAtUtc = now;
        await settingsPointer.ReplaceAsync(settings).ConfigureAwait(false);
        await node.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy)
            .ReplaceAsync(policy).ConfigureAwait(false);
        await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
            .ReplaceAsync(new AetheriaGameSessionState
            {
                Mode = deployment.Mode,
                SessionId = sessionId,
                RunId = deployment.RunId,
                RunRecordKey = deployment.RunRecordKey,
                ControlledEntityKey = run.CurrentEntityKey,
                EntrySurfaceId = AetheriaRuntimeHangarCommands.SurfaceId,
                SimulationRate = 1,
                EffectiveSimulationRate = 1,
                LastStartCommandId = commandId,
                UpdatedAtUtc = now,
                ModePolicyId = policy.PolicyId
            }).ConfigureAwait(false);

        if (string.Equals(deployment.Mode, AetheriaGameModes.Arena, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(controllerRuntimeId) &&
            !string.Equals(controllerRuntimeId, hostRuntimeId, StringComparison.Ordinal))
        {
            await BindArenaControllerCoreAsync(
                node,
                sessionId,
                run.RunId,
                verseId,
                controllerRuntimeId,
                run.CurrentEntityKey,
                DefaultArenaControllerClaims,
                now).ConfigureAwait(false);
        }

        if (string.Equals(deployment.Mode, AetheriaGameModes.Starbridge, StringComparison.Ordinal))
        {
            await node.MutableDocument<AetheriaRuntimeStarbridgeSessionDocument>(AetheriaRuntimeVerseRecordKeys.StarbridgeSessionLatest)
                .ReplaceAsync(new AetheriaRuntimeStarbridgeSessionDocument
                {
                    SessionId = sessionId,
                    ScenarioId = "hangar-deployment",
                    RunId = run.RunId,
                    BaseEntityKey = run.CurrentEntityKey,
                    StationEntityKey = run.CurrentEntityKey,
                    Phase = "active"
                }).ConfigureAwait(false);
        }
    }

    public static Task<AetheriaRuntimeArenaControllerBindingDocument> BindArenaControllerAsync(
        AetheriaStateNode node,
        string sessionId,
        string controllerRuntimeId,
        string controlledEntityKey,
        IReadOnlyList<string> allowedClaimKinds,
        string now)
    {
        return node.CommitAsync(async () =>
        {
            var session = await node.MutableDocument<AetheriaGameSessionState>(AetheriaStateNode.GameSessionStateKey)
                .ReadAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Arena controller binding requires an active game session.");
            if (!string.Equals(session.Mode, AetheriaGameModes.Arena, StringComparison.Ordinal) ||
                !string.Equals(session.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.Equals(session.ModePolicyId, AetheriaModePolicies.ArenaServerAuthoritative, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Arena controller binding requires the active server-authoritative Arena session.");
            }
            var run = await node.MutableDocument<AetheriaRunState>(new CultRecordKey(session.RunRecordKey))
                .ReadAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Arena controller binding requires the active canonical run.");
            var belongsToRun = false;
            foreach (var zoneKey in run.ZoneKeys ?? Array.Empty<string>())
            {
                var zone = await node.MutableDocument<AetheriaZoneState>(new CultRecordKey(zoneKey))
                    .ReadAsync().ConfigureAwait(false);
                if ((zone?.EntityKeys ?? Array.Empty<string>()).Contains(controlledEntityKey, StringComparer.Ordinal))
                {
                    belongsToRun = true;
                    break;
                }
            }
            if (!belongsToRun)
                throw new InvalidOperationException("Arena controller binding target is not an entity in the active canonical run.");
            var policy = await node.MutableDocument<AetheriaRuntimeVerseAuthorityPolicyDocument>(
                    AetheriaRuntimeVerseRecordKeys.VerseAuthorityPolicy)
                .ReadAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("Arena controller binding requires the active server-authority policy.");
            return await BindArenaControllerCoreAsync(
                node,
                session.SessionId,
                run.RunId,
                policy.VerseId,
                controllerRuntimeId,
                controlledEntityKey,
                allowedClaimKinds,
                now).ConfigureAwait(false);
        });
    }

    private static readonly string[] DefaultArenaControllerClaims =
    [
        AetheriaRuntimeClaimKinds.Movement,
        AetheriaRuntimeClaimKinds.Targeting,
        AetheriaRuntimeClaimKinds.Combat,
        AetheriaRuntimeClaimKinds.Interaction
    ];

    private static async Task<AetheriaRuntimeArenaControllerBindingDocument> BindArenaControllerCoreAsync(
        AetheriaStateNode node,
        string sessionId,
        string runId,
        string verseId,
        string controllerRuntimeId,
        string controlledEntityKey,
        IReadOnlyList<string> allowedClaimKinds,
        string now)
    {
        if (string.IsNullOrWhiteSpace(controllerRuntimeId))
            throw new InvalidOperationException("Arena controller runtime identity is required.");
        if (string.IsNullOrWhiteSpace(controlledEntityKey))
            throw new InvalidOperationException("Arena controlled entity identity is required.");
        var claims = (allowedClaimKinds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (claims.Length == 0 || claims.Any(value => !DefaultArenaControllerClaims.Contains(value, StringComparer.Ordinal)))
            throw new InvalidOperationException("Arena controller bindings may grant only movement, targeting, combat, or interaction operation claims.");

        var key = new CultRecordKey(AetheriaRuntimeArenaControllerBindingDocument.RecordKey(sessionId, controlledEntityKey));
        var pointer = node.MutableDocument<AetheriaRuntimeArenaControllerBindingDocument>(key);
        var existing = await pointer.ReadAsync().ConfigureAwait(false);
        var binding = new AetheriaRuntimeArenaControllerBindingDocument
        {
            BindingId = key.ToString(),
            VerseId = string.IsNullOrWhiteSpace(verseId) ? "aetheria.local" : verseId,
            SessionId = sessionId ?? "",
            RunId = runId ?? "",
            ControllerRuntimeId = controllerRuntimeId,
            ControlledEntityKey = controlledEntityKey,
            AllowedClaimKinds = claims,
            Status = AetheriaRuntimeArenaControllerBindingStatuses.Active,
            Revision = Math.Max(0, existing?.Revision ?? 0) + 1,
            UpdatedAtUtc = now ?? ""
        };
        await pointer.ReplaceAsync(binding).ConfigureAwait(false);
        return binding;
    }

    private static async Task<AetheriaDeploymentReceipt?> FindContinuationAsync(
        AetheriaStateNode node,
        string deploymentId)
    {
        var hangar = await node.MutableDocument<AetheriaHangarState>(AetheriaStateNode.HangarKey)
            .ReadAsync().ConfigureAwait(false);
        if (hangar == null)
            return null;
        var draft = await EnsureDraftAsync(node, hangar, DateTimeOffset.UtcNow.ToString("O")).ConfigureAwait(false);
        var shipId = draft.SelectedShipId;
        var ship = (hangar?.Ships ?? []).SingleOrDefault(value =>
            string.Equals(value.ShipId, shipId, StringComparison.Ordinal) &&
            string.Equals(value.ActiveDeploymentId, deploymentId, StringComparison.Ordinal) &&
            string.Equals(value.Status, AetheriaHangarShipStatuses.Deployed, StringComparison.Ordinal));
        if (ship == null)
            return null;
        var deployment = (hangar!.Deployments ?? []).SingleOrDefault(value =>
            value.Accepted &&
            string.Equals(value.DeploymentId, deploymentId, StringComparison.Ordinal) &&
            string.Equals(value.Mode, draft.SelectedMode, StringComparison.Ordinal));
        if (deployment == null)
            return null;
        if (string.IsNullOrWhiteSpace(deployment.RunId) || string.IsNullOrWhiteSpace(deployment.RunRecordKey))
            return null;
        var run = await node.MutableDocument<AetheriaRunState>(new CultRecordKey(deployment.RunRecordKey))
            .ReadAsync().ConfigureAwait(false);
        return run != null &&
               string.Equals(run.RunId, deployment.RunId, StringComparison.Ordinal) &&
               string.Equals(run.GameMode, deployment.Mode, StringComparison.Ordinal)
            ? deployment
            : null;
    }

    private static async Task<AetheriaHangarDraftState> MutateDraftAsync(
        AetheriaStateNode node,
        string now,
        Action<AetheriaHangarDraftState, AetheriaHangarState> mutate)
    {
        return await node.CommitAsync(async () =>
        {
            var hangar = await node.MutableDocument<AetheriaHangarState>(AetheriaStateNode.HangarKey)
                .ReadAsync().ConfigureAwait(false)
                ?? throw new InvalidOperationException("The canonical Hangar document does not exist.");
            var current = await EnsureDraftCoreAsync(node, hangar, now).ConfigureAwait(false);
            var next = new AetheriaHangarDraftState
            {
                Name = current.Name,
                PlayerKey = current.PlayerKey,
                SelectedShipId = current.SelectedShipId,
                SelectedMode = current.SelectedMode,
                ActiveView = current.ActiveView,
                Revision = checked(current.Revision + 1),
                UpdatedAtUtc = now
            };
            mutate(next, hangar);
            await node.MutableDocument<AetheriaHangarDraftState>(AetheriaStateNode.HangarDraftKey)
                .ReplaceAsync(next).ConfigureAwait(false);
            return next;
        }).ConfigureAwait(false);
    }

    private static AetheriaRuntimeEntityLoadoutCommit ToRuntimeLoadout(
        AetheriaDaemonLoadout source,
        string factionKey) => new()
    {
        Name = StarterLoadoutName,
        Kind = "ship",
        FactionKey = factionKey,
        Hull = new AetheriaRuntimeLoadoutItemCommit
        {
            ItemKey = source.HullItemKey,
            Quality = 1,
            Durability = 1,
            Quantity = 1,
            Enabled = true
        },
        Equipment = source.Equipment.Select(ToRuntimeSlot).ToArray(),
        CargoBays = source.CargoBays.Select(ToRuntimeSlot).ToArray(),
        DockingBays = source.DockingBays.Select(ToRuntimeSlot).ToArray(),
        CargoContents = source.CargoBays.Select((_, index) => new AetheriaRuntimeCargoBayLoadoutCommit
        {
            Items = index == 0 ? source.Cargo.Select(ToRuntimeSlot).ToArray() : []
        }).ToArray(),
        DockingBayContents = source.DockingBays.Select(_ => new AetheriaRuntimeCargoBayLoadoutCommit()).ToArray(),
        DockingBayAssignments = source.DockingBays.Select(_ => -1).ToArray(),
        WeaponGroups = source.WeaponGroups.Select(group => (IReadOnlyList<int>)group.ToArray()).ToArray()
    };

    private static AetheriaRuntimeLoadoutItemSlotCommit ToRuntimeSlot(AetheriaEntityItemSlot slot) => new()
    {
        X = slot.Position?.X ?? 0,
        Y = slot.Position?.Y ?? 0,
        Rotation = slot.Rotation,
        Item = new AetheriaRuntimeLoadoutItemCommit
        {
            ItemKey = slot.ItemKey,
            Quality = slot.Quality,
            Durability = slot.Durability,
            Quantity = slot.Quantity,
            Enabled = slot.Enabled,
            OverrideShutdown = slot.OverrideShutdown,
            Temperature = slot.Temperature
        }
    };

    private static AetheriaRuntimeLoadoutItemSlotCommit ToRuntimeSlot(AetheriaLoadoutItemSlot slot) => new()
    {
        X = slot.Position?.X ?? 0,
        Y = slot.Position?.Y ?? 0,
        Rotation = slot.Rotation,
        Item = new AetheriaRuntimeLoadoutItemCommit
        {
            ItemKey = slot.Item?.ItemKey ?? "",
            Quality = slot.Item?.Quality ?? 1,
            Durability = slot.Item?.Durability ?? 1,
            Quantity = slot.Item?.Quantity ?? 1,
            Enabled = slot.Item?.Enabled ?? true,
            OverrideShutdown = slot.Item?.OverrideShutdown ?? false,
            Temperature = slot.Item?.Temperature ?? 0
        }
    };
}
