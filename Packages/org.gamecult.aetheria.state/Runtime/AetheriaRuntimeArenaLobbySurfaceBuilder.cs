using GameCult.Eve.Surface;
using System;
using System.Collections.Generic;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaRuntimeArenaLobbyCommands
    {
        public const string SurfaceId = "aetheria.arena.lobby";
        public const string Join = "aetheria.arena.join";
        public const string ExpectedSessionId = "expectedArenaSessionId";
        public const string ExpectedRunId = "expectedArenaRunId";
    }

    public static class AetheriaRuntimeArenaLobbySurfaceBuilder
    {
        public static EveSurfaceDocument Build(
            string expectedSessionId,
            string expectedRunId,
            string updatedAtUtc,
            long version = 1)
        {
            var join = new EveSurfaceComponent(
                "aetheria.arena.lobby.join",
                "control.button",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["label"] = "JOIN ARENA",
                    ["command"] = AetheriaRuntimeArenaLobbyCommands.Join,
                    ["payload." + AetheriaRuntimeArenaLobbyCommands.ExpectedSessionId] = expectedSessionId ?? "",
                    ["payload." + AetheriaRuntimeArenaLobbyCommands.ExpectedRunId] = expectedRunId ?? ""
                },
                Array.Empty<EveSurfaceComponent>());
            var root = new EveSurfaceComponent(
                "aetheria.arena.lobby.root",
                "surface",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["title"] = "ARENA LOBBY",
                    ["status"] = "An Arena session is accepting controllers."
                },
                new[] { join },
                Array.Empty<GameCult.Mesh.CultMeshStateBindingDescriptor>(),
                Array.Empty<EveEmbeddedDocumentSlot>(),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["direction"] = "vertical",
                    ["minWidth"] = "320",
                    ["minHeight"] = "180",
                    ["padding"] = "16"
                });
            return new EveSurfaceDocument(
                AetheriaRuntimeProviderIdentity.ProviderId,
                "game.arena.lobby",
                "Arena Lobby",
                Math.Max(1, version),
                updatedAtUtc ?? "",
                new EveSurfaceTree(AetheriaRuntimeArenaLobbyCommands.SurfaceId, root, Array.Empty<EveStyleToken>()),
                new[]
                {
                    AetheriaRuntimeSurfaceDocuments.Command(
                        AetheriaRuntimeArenaLobbyCommands.Join,
                        "Join Arena",
                        "cultmesh")
                });
        }
    }
}
