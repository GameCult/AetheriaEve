using GameCult.Caching;
using MessagePack;
using System;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaProgressionSources
    {
        public const string Local = "local";
    }

    public static class AetheriaProgressionSourceStatuses
    {
        public const string Local = "local";
        public const string Ready = "ready";
        public const string Degraded = "degraded";
        public const string Unavailable = "unavailable";
    }

    [CultDocument("gamecult.aetheria.progression_source", "gamecult.aetheria.progression_source.v1")]
    [CultGlobal]
    [MessagePackObject]
    public sealed class AetheriaProgressionSourceDocument
    {
        public const string DocumentKey = "global:gamecult.aetheria.progression_source.v1";

        [Key(0), CultName] public string Name { get; set; } = "Aetheria Progression Source";
        [Key(1)] public string SelectedVerseId { get; set; } = AetheriaProgressionSources.Local;
        [Key(2)] public string[] OdinDiscoveryEndpoints { get; set; } = Array.Empty<string>();
        [Key(3)] public AetheriaProgressionVerseOption[] AvailableVerses { get; set; } = Array.Empty<AetheriaProgressionVerseOption>();
        [Key(4)] public string Status { get; set; } = AetheriaProgressionSourceStatuses.Local;
        [Key(5)] public string Diagnostic { get; set; } = "";
        [Key(6)] public long Revision { get; set; }
        [Key(7)] public string DiscoveredAtUtc { get; set; } = "";
        [Key(8)] public string UpdatedAtUtc { get; set; } = "";

        [IgnoreMember]
        public bool UsesLocalProgression =>
            string.Equals(SelectedVerseId, AetheriaProgressionSources.Local, StringComparison.Ordinal);
    }

    [MessagePackObject]
    public sealed class AetheriaProgressionVerseOption
    {
        [Key(0)] public string VerseId { get; set; } = "";
        [Key(1)] public string DisplayName { get; set; } = "";
        [Key(2)] public string AuthorityModel { get; set; } = "";
        [Key(3)] public string TransportVersion { get; set; } = "";
        [Key(4)] public string RulesHash { get; set; } = "";
        [Key(5)] public string Description { get; set; } = "";
        [Key(6)] public string[] AuthorityRuntimeIds { get; set; } = Array.Empty<string>();
        [Key(7)] public string[] DiscoveryEndpoints { get; set; } = Array.Empty<string>();
    }

    [CultDocument("gamecult.aetheria.hangar_command_envelope", "gamecult.aetheria.hangar_command_envelope.v1")]
    [MessagePackObject]
    public sealed class AetheriaHangarCommandEnvelopeDocument
    {
        [Key(0)] public string CommandId { get; set; } = "";
        [Key(1)] public string PayloadHash { get; set; } = "";
        [Key(2)] public string ClientId { get; set; } = "";
        [Key(3)] public string CreatedAtUtc { get; set; } = "";
        [Key(4)] public string ProgressionVerseId { get; set; } = "";
        [Key(5)] public long ProgressionSourceRevision { get; set; } = -1;
        [Key(6)] public string ProgressionAuthorityRuntimeId { get; set; } = "";
    }

    [CultDocument("gamecult.aetheria.progression_command_route", "gamecult.aetheria.progression_command_route.v1")]
    [MessagePackObject]
    public sealed class AetheriaProgressionCommandRouteDocument
    {
        [Key(0)] public string CommandId { get; set; } = "";
        [Key(1)] public string PayloadHash { get; set; } = "";
        [Key(2)] public string VerseId { get; set; } = "";
        [Key(3)] public string AuthorityRuntimeId { get; set; } = "";
        [Key(4)] public long ProgressionSourceRevision { get; set; }
        [Key(5)] public string[] OdinDiscoveryEndpoints { get; set; } = Array.Empty<string>();
        [Key(6)] public string CreatedAtUtc { get; set; } = "";
        [Key(7)] public string ForwardedInvocationHash { get; set; } = "";
    }
}
