using Aetheria.State.Documents;
using GameCult.Aetheria.State.Verse;

namespace Aetheria.State.Daemon;

/// <summary>
/// Owns Terminus dequeue admission against the live frame that will execute the operation.
/// </summary>
internal static class AetheriaTerminusOperationAdmission
{
    public static IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> AuthorizedCommands(
        IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> pending,
        string sessionId,
        long liveFrameId,
        AetheriaRuntimeVerseAuthorityPolicyDocument? policy,
        IReadOnlyList<AetheriaRuntimeAuthorityLeaseDocument> leases,
        string hostRuntimeId,
        ICollection<string> rejectedCommandIds)
    {
        var eligible = (pending ?? Array.Empty<AetheriaRuntimeDaemonCommandDocument>())
            .Where(command => command != null &&
                string.Equals(command.SessionId, sessionId, StringComparison.Ordinal) &&
                command.ObservedFrameId == liveFrameId)
            .ToArray();
        var eligibleIds = eligible.Select(command => command.CommandId).ToHashSet(StringComparer.Ordinal);
        foreach (var command in pending ?? Array.Empty<AetheriaRuntimeDaemonCommandDocument>())
        {
            if (command != null && !eligibleIds.Contains(command.CommandId))
                rejectedCommandIds.Add(command.CommandId);
        }
        return AetheriaRuntimeAuthorityRouter.AuthorizedCommands(
            eligible,
            policy,
            leases,
            hostRuntimeId,
            rejectedCommandIds);
    }
}
