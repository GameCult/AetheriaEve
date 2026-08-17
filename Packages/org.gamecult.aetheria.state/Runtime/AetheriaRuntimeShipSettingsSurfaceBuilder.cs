using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GameCult.Eve.Surface;

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaRuntimeShipSettingsSurfaceBuilder
    {
        public const string SurfaceId = "aetheria.inventory.current_ship_settings";
        public const string DecrementShutdownThreshold = "aetheria.inventory.current_ship_settings.shutdown.decrement";
        public const string IncrementShutdownThreshold = "aetheria.inventory.current_ship_settings.shutdown.increment";
        public const string ResetShutdownThreshold = "aetheria.inventory.current_ship_settings.shutdown.reset";
        public const string Close = "aetheria.inventory.current_ship_settings.close";

        public static EveSurfaceDocument Build(
            string shipName,
            float shutdownPerformance,
            Func<float, string> formatShutdownPerformance,
            DateTime updatedAtUtc = default(DateTime),
            long version = 1)
        {
            if (updatedAtUtc == default(DateTime))
                updatedAtUtc = DateTime.UtcNow;

            var formattedShutdownPerformance = formatShutdownPerformance == null
                ? shutdownPerformance.ToString("0.###", CultureInfo.InvariantCulture)
                : formatShutdownPerformance(shutdownPerformance);

            return new EveSurfaceDocument(
                providerId: "aetheria",
                providerKind: "inventory.menu",
                title: "Current Ship Settings",
                version: version,
                updatedAtUtc: updatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                surface: new EveSurfaceTree(
                    SurfaceId,
                    Node(
                        $"{SurfaceId}.root",
                        "surface",
                        Array.Empty<(string Key, string Value)>(),
                        Card(
                            $"{SurfaceId}.card",
                            shipName,
                            Metric(
                                $"{SurfaceId}.shutdown.metric",
                                "Shutdown Threshold",
                                formattedShutdownPerformance),
                            Text(
                                $"{SurfaceId}.note",
                                "The observing client supplies selected ship state; shutdown changes are sent as daemon operations."),
                            ButtonRow(
                                $"{SurfaceId}.shutdown.buttons",
                                Button($"{SurfaceId}.shutdown.decrement", "Threshold -", DecrementShutdownThreshold),
                                Button($"{SurfaceId}.shutdown.increment", "Threshold +", IncrementShutdownThreshold),
                                Button($"{SurfaceId}.shutdown.reset", "Default", ResetShutdownThreshold),
                                Button($"{SurfaceId}.close", "Close", Close)))),
                    Array.Empty<EveStyleToken>()),
                commands: new[]
                {
                    AetheriaRuntimeSurfaceDocuments.Command(DecrementShutdownThreshold, "Threshold -", "cultmesh"),
                    AetheriaRuntimeSurfaceDocuments.Command(IncrementShutdownThreshold, "Threshold +", "cultmesh"),
                    AetheriaRuntimeSurfaceDocuments.Command(ResetShutdownThreshold, "Default", "cultmesh"),
                    AetheriaRuntimeSurfaceDocuments.Command(Close, "Close", "cultmesh")
                });
        }

        private static EveSurfaceComponent Card(
            string id,
            string title,
            params EveSurfaceComponent[] children)
        {
            return Node(id, "card", new[] { ("title", title ?? "") }, children);
        }

        private static EveSurfaceComponent Metric(string id, string label, string value)
        {
            return Node(id, "metric", new[] { ("label", label ?? ""), ("value", value ?? "") });
        }

        private static EveSurfaceComponent Text(string id, string value)
        {
            return Node(id, "text", new[] { ("value", value ?? "") });
        }

        private static EveSurfaceComponent Button(string id, string label, string command)
        {
            return Node(id, "control.button", new[] { ("label", label ?? ""), ("command", command ?? "") });
        }

        private static EveSurfaceComponent ButtonRow(
            string id,
            params EveSurfaceComponent[] children)
        {
            return Node(id, "control.row", Array.Empty<(string Key, string Value)>(), children);
        }

        private static EveSurfaceComponent Node(
            string id,
            string kind,
            IEnumerable<(string Key, string Value)> props,
            params EveSurfaceComponent[] children)
        {
            return new EveSurfaceComponent(
                id ?? "",
                kind ?? "",
                (props ?? Array.Empty<(string Key, string Value)>())
                    .ToDictionary(prop => prop.Key, prop => prop.Value ?? "", StringComparer.Ordinal),
                children ?? Array.Empty<EveSurfaceComponent>());
        }
    }

    public enum AetheriaRuntimeShipSettingsCommandKind
    {
        Unknown = 0,
        DecrementShutdownThreshold = 1,
        IncrementShutdownThreshold = 2,
        ResetShutdownThreshold = 3,
        Close = 4
    }

    public readonly struct AetheriaRuntimeShipSettingsCommand
    {
        public AetheriaRuntimeShipSettingsCommand(AetheriaRuntimeShipSettingsCommandKind kind)
        {
            Kind = kind;
        }

        public AetheriaRuntimeShipSettingsCommandKind Kind { get; }
    }

    public static class AetheriaRuntimeShipSettingsSurfaceCommands
    {
        public const float DefaultShutdownThresholdStep = 0.05f;

        public static bool TryRead(
            EveSurfaceCommandRequest request,
            out AetheriaRuntimeShipSettingsCommand command)
        {
            command = default;
            if (request == null ||
                !string.Equals(request.SurfaceId, AetheriaRuntimeShipSettingsSurfaceBuilder.SurfaceId, StringComparison.Ordinal))
                return false;

            switch (request.Operation?.OperationId ?? "")
            {
                case AetheriaRuntimeShipSettingsSurfaceBuilder.DecrementShutdownThreshold:
                    command = new AetheriaRuntimeShipSettingsCommand(
                        AetheriaRuntimeShipSettingsCommandKind.DecrementShutdownThreshold);
                    return true;
                case AetheriaRuntimeShipSettingsSurfaceBuilder.IncrementShutdownThreshold:
                    command = new AetheriaRuntimeShipSettingsCommand(
                        AetheriaRuntimeShipSettingsCommandKind.IncrementShutdownThreshold);
                    return true;
                case AetheriaRuntimeShipSettingsSurfaceBuilder.ResetShutdownThreshold:
                    command = new AetheriaRuntimeShipSettingsCommand(
                        AetheriaRuntimeShipSettingsCommandKind.ResetShutdownThreshold);
                    return true;
                case AetheriaRuntimeShipSettingsSurfaceBuilder.Close:
                    command = new AetheriaRuntimeShipSettingsCommand(
                        AetheriaRuntimeShipSettingsCommandKind.Close);
                    return true;
                default:
                    return false;
            }
        }

        public static float ResolveShutdownPerformance(
            AetheriaRuntimeShipSettingsCommandKind kind,
            float currentShutdownPerformance,
            float defaultShutdownPerformance,
            float shutdownThresholdStep = DefaultShutdownThresholdStep)
        {
            switch (kind)
            {
                case AetheriaRuntimeShipSettingsCommandKind.DecrementShutdownThreshold:
                    return ClampShutdownPerformance(currentShutdownPerformance - shutdownThresholdStep);
                case AetheriaRuntimeShipSettingsCommandKind.IncrementShutdownThreshold:
                    return ClampShutdownPerformance(currentShutdownPerformance + shutdownThresholdStep);
                case AetheriaRuntimeShipSettingsCommandKind.ResetShutdownThreshold:
                    return ClampShutdownPerformance(defaultShutdownPerformance);
                default:
                    return ClampShutdownPerformance(currentShutdownPerformance);
            }
        }

        public static float ClampShutdownPerformance(float value)
        {
            if (float.IsNaN(value))
                return 0f;
            if (value <= 0f)
                return 0f;
            return value >= 1f ? 1f : value;
        }
    }
}
