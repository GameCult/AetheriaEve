using System;
using System.Collections.Generic;
using System.Linq;
using GameCult.Eve.Surface;
using GameCult.Mesh;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaRuntimeSurfaceStateRefs
    {
        public const string Source = "stateRef";
        public const string Value = "valueRef";
        public const string Label = "labelRef";
        public const string Format = "stateFormat";

        public static (string Key, string Value) SourceRef(string reference) =>
            (Source, reference ?? "");

        public static (string Key, string Value) ValueRef(string reference) =>
            (Value, reference ?? "");

        public static (string Key, string Value) LabelRef(string reference) =>
            (Label, reference ?? "");

        public static (string Key, string Value) FormatRef(string format) =>
            (Format, format ?? "");
    }

    public static class AetheriaRuntimeSurfaceStateBindings
    {
        public static CultMeshStateBindingDescriptor ForDaemonStateRef(
            string targetProp,
            string stateRef,
            string schemaId = AetheriaRuntimeDaemonSchemas.Frame)
        {
            return new CultMeshStateBindingDescriptor(
                targetProp,
                ToPointerId(stateRef),
                stateRef,
                schemaId,
                new CultMeshRouteHint(
                    CultMeshLocalityKind.SharedMemory,
                    "daemon-published CultCache state"));
        }

        public static IReadOnlyList<CultMeshStateBindingDescriptor> FromProps(
            IReadOnlyDictionary<string, string> props)
        {
            if (props == null || props.Count == 0)
                return Array.Empty<CultMeshStateBindingDescriptor>();

            var bindings = new List<CultMeshStateBindingDescriptor>();
            AddPropBinding(bindings, "value", Get(props, AetheriaRuntimeSurfaceStateRefs.Source));
            foreach (var prop in props)
            {
                if (string.IsNullOrWhiteSpace(prop.Value) ||
                    string.Equals(prop.Key, AetheriaRuntimeSurfaceStateRefs.Source, StringComparison.Ordinal) ||
                    !prop.Key.EndsWith("Ref", StringComparison.Ordinal))
                {
                    continue;
                }

                AddPropBinding(bindings, prop.Key.Substring(0, prop.Key.Length - "Ref".Length), prop.Value);
            }

            return bindings;
        }

        private static void AddPropBinding(
            List<CultMeshStateBindingDescriptor> bindings,
            string targetProp,
            string stateRef)
        {
            if (string.IsNullOrWhiteSpace(stateRef))
                return;

            var schemaId = stateRef.StartsWith(AetheriaRuntimeDaemonItemStatQueries.StateRefPrefix + "/", StringComparison.Ordinal)
                ? AetheriaRuntimeDaemonSchemas.CurrentEntity
                : AetheriaRuntimeDaemonSchemas.Frame;
            bindings.Add(ForDaemonStateRef(targetProp, stateRef, schemaId));
        }

        private static string Get(IReadOnlyDictionary<string, string> props, string key) =>
            props.TryGetValue(key, out var value) ? value : "";

        private static string ToPointerId(string stateRef)
        {
            if (string.IsNullOrWhiteSpace(stateRef))
                return "";

            var chars = stateRef
                .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '.')
                .ToArray();
            var pointerId = new string(chars).Trim('.');
            while (pointerId.Contains("..", StringComparison.Ordinal))
                pointerId = pointerId.Replace("..", ".", StringComparison.Ordinal);
            return string.IsNullOrWhiteSpace(pointerId) ? "aetheria.state.unknown" : pointerId;
        }
    }

    public static class AetheriaRuntimeSurfaceDocuments
    {
        public static EveCommandTemplate Command(string command, string label, string transport)
        {
            return new EveCommandTemplate(CultMesh.OperationBindingRecord(
                command,
                label,
                "",
                nameof(CultMeshLocalityKind.Automatic),
                transport).ToBinding());
        }

        public static EveCommandTemplate Command(CultMeshOperationBindingDescriptor operation) =>
            new EveCommandTemplate(operation);
    }
}
