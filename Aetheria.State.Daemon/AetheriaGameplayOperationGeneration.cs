using GameCult.Aetheria.State.Verse;

namespace Aetheria.State.Daemon;

/// <summary>
/// Owns the session/run/frame gate that every gameplay mode crosses before its
/// authority policy can admit an operation.
/// </summary>
internal static class AetheriaGameplayOperationGeneration
{
    public static IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> SelectCurrent(
        IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> pending,
        string sessionId,
        string runId,
        long liveFrameId,
        ICollection<string> rejectedCommandIds)
    {
        var accepted = new List<AetheriaRuntimeDaemonCommandDocument>();
        foreach (var command in pending ?? Array.Empty<AetheriaRuntimeDaemonCommandDocument>())
        {
            if (command != null &&
                string.Equals(command.SessionId, sessionId, StringComparison.Ordinal) &&
                string.Equals(command.RunId, runId, StringComparison.Ordinal) &&
                command.ObservedFrameId == liveFrameId)
            {
                accepted.Add(command);
            }
            else if (!string.IsNullOrWhiteSpace(command?.CommandId))
            {
                rejectedCommandIds.Add(command.CommandId);
            }
        }
        return accepted;
    }
}
