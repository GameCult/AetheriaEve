using System;
using System.Linq;
using GameCult.Caching;
using MessagePack;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaGameModes
    {
        public const string Terminus = "terminus";
        public const string Starbridge = "starbridge";
        public const string Arena = "arena";

        public static bool IsKnown(string? mode) =>
            string.Equals(mode, Terminus, StringComparison.Ordinal) ||
            string.Equals(mode, Starbridge, StringComparison.Ordinal) ||
            string.Equals(mode, Arena, StringComparison.Ordinal);
    }

    public static class AetheriaModePolicies
    {
        public const string ArenaServerAuthoritative = "aetheria.mode.arena.server.v1";

        // A blank id means that mode's distinct CultMesh authority policy has not
        // yet earned a product contract. Arena is the first installed mode policy.
        public static string ForMode(string? mode) =>
            string.Equals(mode, AetheriaGameModes.Arena, StringComparison.Ordinal)
                ? ArenaServerAuthoritative
                : "";
    }

    public static class AetheriaHangarShipStatuses
    {
        public const string Available = "available";
        public const string Deployed = "deployed";
        public const string Lost = "lost";
    }

    public static class AetheriaHangarViews
    {
        public const string Overview = "overview";
        public const string Loadout = "loadout";

        public static bool IsKnown(string? view) =>
            string.Equals(view, Overview, StringComparison.Ordinal) ||
            string.Equals(view, Loadout, StringComparison.Ordinal);
    }

    [CultDocument("gamecult.aetheria.hangar_draft", "gamecult.aetheria.hangar_draft.v1")]
    [CultGlobal]
    [MessagePackObject]
    public sealed class AetheriaHangarDraftState
    {
        [Key(0), CultName] public string Name { get; set; } = "Hangar selection";
        [Key(1)] public string PlayerKey { get; set; } = "";
        [Key(2)] public string SelectedShipId { get; set; } = "";
        [Key(3)] public string SelectedMode { get; set; } = AetheriaGameModes.Terminus;
        [Key(4)] public string ActiveView { get; set; } = AetheriaHangarViews.Overview;
        [Key(5)] public long Revision { get; set; }
        [Key(6)] public string UpdatedAtUtc { get; set; } = "";
    }

    [CultDocument("gamecult.aetheria.hangar", "gamecult.aetheria.hangar.v1")]
    [CultGlobal]
    [MessagePackObject]
    public sealed class AetheriaHangarState
    {
        [Key(0), CultName] public string Name { get; set; } = "Aetheria Hangar";
        [Key(1)] public string HangarId { get; set; } = "local";
        [Key(2)] public string PlayerKey { get; set; } = "";
        [Key(3)] public long Revision { get; set; }
        [Key(4)] public AetheriaHangarShip[] Ships { get; set; } = Array.Empty<AetheriaHangarShip>();
        [Key(5)] public AetheriaHangarItemStack[] Inventory { get; set; } = Array.Empty<AetheriaHangarItemStack>();
        [Key(6)] public AetheriaHangarCurrency[] Currencies { get; set; } = Array.Empty<AetheriaHangarCurrency>();
        [Key(7)] public string[] UnlockKeys { get; set; } = Array.Empty<string>();
        [Key(8)] public string[] LoadoutTemplateKeys { get; set; } = Array.Empty<string>();
        [Key(9)] public AetheriaDeploymentReceipt[] Deployments { get; set; } = Array.Empty<AetheriaDeploymentReceipt>();
        [Key(10)] public string UpdatedAtUtc { get; set; } = "";
    }

    [CultDocument("gamecult.aetheria.hangar_projection", "gamecult.aetheria.hangar_projection.v1")]
    [CultGlobal]
    [MessagePackObject]
    public sealed class AetheriaHangarProjectionDocument
    {
        [Key(0), CultName] public string Name { get; set; } = "Aetheria Hangar projection";
        [Key(1)] public long Generation { get; set; }
        [Key(2)] public string AuthorityRuntimeId { get; set; } = "";
        [Key(3)] public string AssetVerseId { get; set; } = "";
        [Key(4)] public string AssetProviderId { get; set; } = "";
        [Key(5)] public string AssetManifestRecordRef { get; set; } = "";
        [Key(6)] public AetheriaHangarState Hangar { get; set; } = new AetheriaHangarState();
        [Key(7)] public AetheriaHangarDraftState Draft { get; set; } = new AetheriaHangarDraftState();
        [Key(8)] public AetheriaRuntimeLoadoutTemplateCommit? Loadout { get; set; }
        [Key(9)] public AetheriaRuntimeCatalogSnapshot? Catalog { get; set; }
        [Key(10)] public string UpdatedAtUtc { get; set; } = "";
        [Key(11)] public long AssetCatalogVersion { get; set; }
    }

    [MessagePackObject]
    public sealed class AetheriaHangarShip
    {
        [Key(0)] public string ShipId { get; set; } = "";
        [Key(1)] public string HullItemKey { get; set; } = "";
        [Key(2)] public string LoadoutTemplateKey { get; set; } = "";
        [Key(3)] public string Status { get; set; } = AetheriaHangarShipStatuses.Available;
        [Key(4)] public string ActiveDeploymentId { get; set; } = "";
    }

    [MessagePackObject]
    public sealed class AetheriaHangarItemStack
    {
        [Key(0)] public string ItemKey { get; set; } = "";
        [Key(1)] public long Quantity { get; set; }
    }

    [MessagePackObject]
    public sealed class AetheriaHangarCurrency
    {
        [Key(0)] public string CurrencyKey { get; set; } = "";
        [Key(1)] public long Quantity { get; set; }
    }

    [MessagePackObject]
    public sealed class AetheriaDeploymentRequest
    {
        [Key(0)] public string RequestId { get; set; } = "";
        [Key(1)] public string PlayerKey { get; set; } = "";
        [Key(2)] public string Mode { get; set; } = AetheriaGameModes.Terminus;
        [Key(3)] public string ShipId { get; set; } = "";
        [Key(4)] public string LoadoutTemplateKey { get; set; } = "";
        [Key(5)] public long ExpectedHangarRevision { get; set; }
        [Key(6)] public string ModePolicyId { get; set; } = "";
    }

    [MessagePackObject]
    public sealed class AetheriaDeploymentReceipt
    {
        [Key(0)] public string DeploymentId { get; set; } = "";
        [Key(1)] public string RequestId { get; set; } = "";
        [Key(2)] public bool Accepted { get; set; }
        [Key(3)] public string Diagnostic { get; set; } = "";
        [Key(4)] public string PlayerKey { get; set; } = "";
        [Key(5)] public string Mode { get; set; } = "";
        [Key(6)] public string ShipId { get; set; } = "";
        [Key(7)] public string LoadoutTemplateKey { get; set; } = "";
        [Key(8)] public long HangarRevision { get; set; }
        [Key(9)] public string ModePolicyId { get; set; } = "";
        [Key(10)] public AetheriaRuntimeEntityLoadoutCommit Loadout { get; set; } = new AetheriaRuntimeEntityLoadoutCommit();
        [Key(11)] public string CommittedAtUtc { get; set; } = "";
        [Key(12)] public string RunId { get; set; } = "";
        [Key(13)] public string RunRecordKey { get; set; } = "";
        [Key(14)] public long RequestedHangarRevision { get; set; }
    }
}
