using GameCult.Eve.Surface;
using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Mesh;

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaRuntimePlayerSettingsSurfaceBuilder
    {
        public static EveSurfaceDocument Build(
            AetheriaRuntimePlayerSettingsDocument settings,
            string updatedAtUtc,
            long version = 1)
        {
            return Build(
                settings?.PlayerName ?? "",
                settings?.TutorialPassed ?? false,
                activeRunKey: "",
                settings?.TemperatureUnit ?? "",
                Math.Max(0, settings?.SignificantDigits ?? 0),
                settings?.NebulaQuality ?? "",
                settings?.ShowAsteroidsInMinimap ?? false,
                updatedAtUtc,
                version);
        }

        public static EveSurfaceDocument Build(
            string playerName,
            bool tutorialPassed,
            string activeRunKey,
            string temperatureUnit,
            int significantDigits,
            string nebulaQuality,
            bool showAsteroidsInMinimap,
            string updatedAtUtc,
            long version = 1)
        {
            playerName ??= "";
            activeRunKey ??= "";
            temperatureUnit ??= "";
            nebulaQuality ??= "";
            updatedAtUtc ??= "";
            significantDigits = Math.Max(0, significantDigits);
            return new EveSurfaceDocument(
                providerId: "aetheria",
                providerKind: "game.runtime",
                title: "Aetheria Player Settings",
                version: version,
                updatedAtUtc: updatedAtUtc,
                surface: new EveSurfaceTree(
                    AetheriaRuntimePlayerSettingsCommands.SurfaceId,
                    Node(
                        "aetheria.playerSettings.root",
                        "surface",
                        Array.Empty<(string Key, string Value)>(),
                        Node(
                            "aetheria.playerSettings.summary",
                            "card",
                            new[] { ("title", "Player Settings") },
                            TextInput(
                                "playerSettings.summary.playerName",
                                "Name",
                                playerName,
                                AetheriaRuntimePlayerSettingsCommands.SetPlayerName),
                            Row(
                                "playerSettings.summary.values",
                                ("tutorialPassed", tutorialPassed ? "Yes" : "No"),
                                ("activeRun", activeRunKey)),
                            Text(
                                "playerSettings.summary.note",
                                "Input remapping lowers through the runtime Eve input screen and sends typed input-setting requests.")),
                        Node(
                            "aetheria.playerSettings.gameplay",
                            "card",
                            new[] { ("title", "Gameplay") },
                            Metric("playerSettings.gameplay.temperatureUnit", "Temperature Unit", temperatureUnit),
                            ButtonRow(
                                "playerSettings.gameplay.temperatureUnit.buttons",
                                Button(
                                    "playerSettings.gameplay.temperatureUnit.cycle",
                                    "Cycle Temperature Unit",
                                    AetheriaRuntimePlayerSettingsCommands.CycleTemperatureUnit)),
                            Metric(
                                "playerSettings.gameplay.significantDigits",
                                "Significant Digits",
                                significantDigits.ToString()),
                            ButtonRow(
                                "playerSettings.gameplay.significantDigits.buttons",
                                Button(
                                    "playerSettings.gameplay.significantDigits.decrement",
                                    "Digits -",
                                    AetheriaRuntimePlayerSettingsCommands.DecrementSignificantDigits),
                                Button(
                                    "playerSettings.gameplay.significantDigits.increment",
                                    "Digits +",
                                    AetheriaRuntimePlayerSettingsCommands.IncrementSignificantDigits))),
                        Node(
                            "aetheria.playerSettings.graphics",
                            "card",
                            new[] { ("title", "Graphics") },
                            Metric("playerSettings.graphics.nebulaQuality", "Nebula Quality", nebulaQuality),
                            ButtonRow(
                                "playerSettings.graphics.nebulaQuality.buttons",
                                Button(
                                    "playerSettings.graphics.nebulaQuality.cycle",
                                    "Cycle Nebula Quality",
                                    AetheriaRuntimePlayerSettingsCommands.CycleNebulaQuality)),
                            Metric(
                                "playerSettings.graphics.showAsteroids",
                                "Show Asteroids In Minimap",
                                showAsteroidsInMinimap ? "Enabled" : "Disabled"),
                            ButtonRow(
                                "playerSettings.graphics.showAsteroids.buttons",
                                Button(
                                    "playerSettings.graphics.showAsteroids.toggle",
                                    showAsteroidsInMinimap ? "Disable Minimap Asteroids" : "Enable Minimap Asteroids",
                                    AetheriaRuntimePlayerSettingsCommands.ToggleShowAsteroidsInMinimap)))),
                    Array.Empty<EveStyleToken>()),
                commands: new[]
                {
                    AetheriaRuntimeSurfaceDocuments.Command(
                        AetheriaRuntimePlayerSettingsCommands.Refresh,
                        "Refresh",
                        "cultmesh"),
                    AetheriaRuntimeSurfaceDocuments.Command(
                        AetheriaRuntimePlayerSettingsCommands.SetPlayerName,
                        "Set Player Name",
                        "cultmesh"),
                    AetheriaRuntimeSurfaceDocuments.Command(
                        AetheriaRuntimePlayerSettingsCommands.CycleTemperatureUnit,
                        "Cycle Temperature Unit",
                        "cultmesh"),
                    AetheriaRuntimeSurfaceDocuments.Command(
                        AetheriaRuntimePlayerSettingsCommands.DecrementSignificantDigits,
                        "Digits -",
                        "cultmesh"),
                    AetheriaRuntimeSurfaceDocuments.Command(
                        AetheriaRuntimePlayerSettingsCommands.IncrementSignificantDigits,
                        "Digits +",
                        "cultmesh"),
                    AetheriaRuntimeSurfaceDocuments.Command(
                        AetheriaRuntimePlayerSettingsCommands.CycleNebulaQuality,
                        "Cycle Nebula Quality",
                        "cultmesh"),
                    AetheriaRuntimeSurfaceDocuments.Command(
                        AetheriaRuntimePlayerSettingsCommands.ToggleShowAsteroidsInMinimap,
                        "Toggle Minimap Asteroids",
                        "cultmesh")
                });
        }

        private static EveSurfaceComponent Metric(string id, string label, string value)
        {
            return Node(id, "metric", new[] { ("label", label), ("value", value) });
        }

        private static EveSurfaceComponent Text(string id, string value)
        {
            return Node(id, "text", new[] { ("value", value) });
        }

        private static EveSurfaceComponent Button(string id, string label, string command)
        {
            return Node(id, "control.button", new[] { ("label", label), ("command", command) });
        }

        private static EveSurfaceComponent TextInput(string id, string label, string value, string command)
        {
            return Node(id, "control.text", new[] { ("label", label), ("value", value), ("command", command) });
        }

        private static EveSurfaceComponent ButtonRow(
            string id,
            params EveSurfaceComponent[] children)
        {
            return Node(id, "row", Array.Empty<(string Key, string Value)>(), children);
        }

        private static EveSurfaceComponent Row(
            string id,
            params (string Key, string Value)[] props)
        {
            return Node(id, "row", props);
        }

        private static EveSurfaceComponent Node(
            string id,
            string kind,
            IEnumerable<(string Key, string Value)> props,
            params EveSurfaceComponent[] children)
        {
            return new EveSurfaceComponent(
                id,
                kind,
                props.ToDictionary(prop => prop.Key, prop => prop.Value, StringComparer.Ordinal),
                children ?? Array.Empty<EveSurfaceComponent>());
        }
    }
}
