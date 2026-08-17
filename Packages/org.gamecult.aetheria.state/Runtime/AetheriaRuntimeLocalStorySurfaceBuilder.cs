using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Eve.Surface;

namespace GameCult.Aetheria.State.Verse
{
    public sealed class AetheriaRuntimeLocalStoryChoiceState
    {
        public AetheriaRuntimeLocalStoryChoiceState(int index, string label)
        {
            Index = index;
            Label = label ?? "";
        }

        public int Index { get; }
        public string Label { get; }
    }

    public static class AetheriaRuntimeLocalStorySurfaceBuilder
    {
        public const string SurfaceId = "aetheria.runtime_menu.local_story";
        public const string Continue = "aetheria.runtime_menu.local_story.continue";
        private const string ChoicePrefix = "aetheria.runtime_menu.local_story.choice.";

        public static string ChoiceCommandFor(int choiceIndex)
        {
            return $"{ChoicePrefix}{Math.Max(0, choiceIndex)}";
        }

        public static EveSurfaceDocument Build(
            string locationLabel,
            string currentPath,
            string body,
            bool canContinue,
            IEnumerable<AetheriaRuntimeLocalStoryChoiceState> choices,
            string updatedAtUtc,
            long version = 1)
        {
            var storyBody = string.IsNullOrWhiteSpace(body) ? "..." : body.Trim();
            var orderedChoices = (choices ?? Array.Empty<AetheriaRuntimeLocalStoryChoiceState>())
                .Where(choice => choice != null)
                .OrderBy(choice => choice.Index)
                .ToArray();

            var commands = new List<EveCommandTemplate>();
            var controls = new List<EveSurfaceComponent>();
            if (canContinue)
            {
                commands.Add(AetheriaRuntimeSurfaceDocuments.Command(
                    Continue,
                    "Continue",
                    "cultmesh"));
                controls.Add(Button($"{SurfaceId}.continue", "Continue", Continue));
            }

            foreach (var choice in orderedChoices)
            {
                var command = ChoiceCommandFor(choice.Index);
                commands.Add(AetheriaRuntimeSurfaceDocuments.Command(
                    command,
                    choice.Label,
                    "cultmesh"));
                controls.Add(Button($"{SurfaceId}.choice.{choice.Index}", choice.Label, command));
            }

            var children = new List<EveSurfaceComponent>
            {
                Card(
                    $"{SurfaceId}.card",
                    string.IsNullOrWhiteSpace(locationLabel) ? "Local" : locationLabel,
                    Text($"{SurfaceId}.body", storyBody),
                    Metric($"{SurfaceId}.path", "Path", string.IsNullOrWhiteSpace(currentPath) ? "root" : currentPath))
            };
            if (controls.Count > 0)
                children.Add(ButtonColumn($"{SurfaceId}.controls", controls.ToArray()));

            return new EveSurfaceDocument(
                providerId: "aetheria",
                providerKind: "runtime.menu",
                title: "Local Story",
                version: version,
                updatedAtUtc: updatedAtUtc ?? "",
                surface: new EveSurfaceTree(
                    SurfaceId,
                    Node(
                        $"{SurfaceId}.root",
                        "surface",
                        Array.Empty<(string Key, string Value)>(),
                        children.ToArray()),
                    Array.Empty<EveStyleToken>()),
                commands: commands.ToArray());
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

        private static EveSurfaceComponent ButtonColumn(
            string id,
            params EveSurfaceComponent[] children)
        {
            return Node(id, "control.column", Array.Empty<(string Key, string Value)>(), children);
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

    public enum AetheriaRuntimeLocalStoryCommandKind
    {
        Unknown = 0,
        Continue = 1,
        Choose = 2
    }

    public readonly struct AetheriaRuntimeLocalStoryCommand
    {
        public AetheriaRuntimeLocalStoryCommand(
            AetheriaRuntimeLocalStoryCommandKind kind,
            int choiceIndex)
        {
            Kind = kind;
            ChoiceIndex = choiceIndex;
        }

        public AetheriaRuntimeLocalStoryCommandKind Kind { get; }
        public int ChoiceIndex { get; }
    }

    public static class AetheriaRuntimeLocalStorySurfaceCommands
    {
        private const string ChoicePrefix = "aetheria.runtime_menu.local_story.choice.";

        public static bool TryRead(
            EveSurfaceCommandRequest request,
            out AetheriaRuntimeLocalStoryCommand command)
        {
            command = default;
            if (request == null ||
                !string.Equals(request.SurfaceId, AetheriaRuntimeLocalStorySurfaceBuilder.SurfaceId, StringComparison.Ordinal))
                return false;

            var commandText = request.Operation?.OperationId ?? "";
            if (string.Equals(commandText, AetheriaRuntimeLocalStorySurfaceBuilder.Continue, StringComparison.Ordinal))
            {
                command = new AetheriaRuntimeLocalStoryCommand(
                    AetheriaRuntimeLocalStoryCommandKind.Continue,
                    -1);
                return true;
            }

            if (!commandText.StartsWith(ChoicePrefix, StringComparison.Ordinal) ||
                !int.TryParse(commandText.Substring(ChoicePrefix.Length), out var choiceIndex))
                return false;

            command = new AetheriaRuntimeLocalStoryCommand(
                AetheriaRuntimeLocalStoryCommandKind.Choose,
                choiceIndex);
            return true;
        }
    }
}
