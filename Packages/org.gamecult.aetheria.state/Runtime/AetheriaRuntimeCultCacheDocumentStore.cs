using GameCult.Caching;
using GameCult.Mesh;
using MessagePack;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    internal static class AetheriaRuntimeCultCacheDocumentStore
    {
        public static void WriteEveCommand(string path, AetheriaRuntimeEveCommandDocument document)
        {
            WriteDocument(
                path,
                $"command:gamecult.eve.command.{document.CommandId}.v1",
                AetheriaRuntimeEveCommandDocument.SchemaId,
                "gamecult.eve.command",
                "gamecult.eve.command.v1",
                document.IssuedAtUtc,
                MessagePackSerializer.Serialize(document));
        }

        public static AetheriaRuntimeEveCommandDocument ReadEveCommand(string path)
        {
            return MessagePackSerializer.Deserialize<AetheriaRuntimeEveCommandDocument>(
                CultMesh.ReadSingleFileDocumentPayload(path, AetheriaRuntimeEveCommandDocument.SchemaId));
        }

        public static void WriteDaemonCommand(string path, AetheriaRuntimeDaemonCommandDocument document)
        {
            WriteDocument(
                path,
                $"command:gamecult.aetheria.daemon_command.{document.CommandId}.v1",
                AetheriaRuntimeDaemonSchemas.Command,
                "gamecult.aetheria.daemon_command",
                "gamecult.aetheria.daemon_command.v1",
                document.IssuedAtUtc,
                MessagePackSerializer.Serialize(document));
        }

        public static AetheriaRuntimeDaemonCommandDocument ReadDaemonCommand(string path)
        {
            return MessagePackSerializer.Deserialize<AetheriaRuntimeDaemonCommandDocument>(
                CultMesh.ReadSingleFileDocumentPayload(path, AetheriaRuntimeDaemonSchemas.Command));
        }

        private static void WriteDocument(
            string path,
            string key,
            string schemaId,
            string schemaName,
            string schemaVersion,
            string storedAt,
            byte[] payload)
        {
            CultMesh.WriteSingleFileDocumentPayload(
                path,
                new CultRecordKey(key),
                new CultMeshSingleFileDocumentSchema(schemaId, schemaName, schemaVersion),
                storedAt,
                payload);
        }
    }
}
