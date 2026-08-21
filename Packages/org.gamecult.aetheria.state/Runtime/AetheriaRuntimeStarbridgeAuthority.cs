using System;
using System.Collections.Generic;
using System.Linq;

#nullable enable

namespace GameCult.Aetheria.State.Verse
{
    public sealed class AetheriaRuntimeStarbridgeOperationSelection
    {
        public AetheriaRuntimeStarbridgeOperationSelection(
            IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> selected,
            IReadOnlyList<string> rejectedCommandIds)
        {
            Selected = selected ?? Array.Empty<AetheriaRuntimeDaemonCommandDocument>();
            RejectedCommandIds = rejectedCommandIds ?? Array.Empty<string>();
        }

        public IReadOnlyList<AetheriaRuntimeDaemonCommandDocument> Selected { get; }
        public IReadOnlyList<string> RejectedCommandIds { get; }
    }

    /// <summary>
    /// Owns bounded Starbridge operation admission. The Commander daemon remains the only
    /// simulator and fact committer. A bound Pilot may control movement input for its ship and
    /// exact observed frame; this is not independently simulated candidate fact finality.
    /// </summary>
    public static class AetheriaRuntimeStarbridgeOperationAdmission
    {
        public static bool TryResolvePilotSeat(
            IEnumerable<AetheriaRuntimeStarbridgePlayerSeatDocument>? seats,
            string sessionId,
            string runId,
            string runtimeId,
            AetheriaRuntimeRunCheckpointCommit? run,
            out AetheriaRuntimeStarbridgePlayerSeatDocument seat,
            out string controlledEntityKey)
        {
            seat = null!;
            controlledEntityKey = "";
            if (run == null)
                return false;
            var matches = (seats ?? Enumerable.Empty<AetheriaRuntimeStarbridgePlayerSeatDocument>())
                .Where(value => value != null &&
                    string.Equals(value.SessionId, sessionId ?? "", StringComparison.Ordinal) &&
                    string.Equals(value.RunId, runId ?? "", StringComparison.Ordinal) &&
                    string.Equals(value.Role, AetheriaRuntimeStarbridgePlayerSeatRoles.Pilot, StringComparison.Ordinal) &&
                    string.Equals(value.RuntimeId, runtimeId ?? "", StringComparison.Ordinal) &&
                    string.Equals(value.ConnectionState, AetheriaRuntimeStarbridgePlayerSeatConnectionStates.Connected, StringComparison.Ordinal) &&
                    !string.IsNullOrWhiteSpace(value.ControlledEntityId) &&
                    run.TryResolveEntityId(value.ControlledEntityId, out _))
                .ToArray();
            if (matches.Length != 1 || !run.TryResolveEntityId(matches[0].ControlledEntityId, out controlledEntityKey))
                return false;
            seat = matches[0];
            return true;
        }

        public static bool IsPilotInputActive(
            string gameMode,
            string modePolicyId,
            AetheriaRuntimeVerseAuthorityPolicyDocument? policy,
            string hostRuntimeId) =>
            string.Equals(gameMode, AetheriaGameModes.Starbridge, StringComparison.Ordinal) &&
            string.Equals(modePolicyId, AetheriaModePolicies.StarbridgeCommanderPilotInput, StringComparison.Ordinal) &&
            policy != null &&
            string.Equals(policy.PolicyId, modePolicyId, StringComparison.Ordinal) &&
            string.Equals(policy.HostRuntimeId, hostRuntimeId ?? "", StringComparison.Ordinal) &&
            string.Equals(policy.DefaultMode, AetheriaRuntimeAuthorityModes.HostAuthoritative, StringComparison.Ordinal);

        public static string OperationSlotKey(
            string sessionId,
            long frameId,
            string subjectKey,
            string claimKind) =>
            string.Join("\u001f", sessionId ?? "", frameId.ToString(
                System.Globalization.CultureInfo.InvariantCulture), subjectKey ?? "", claimKind ?? "");

        public static AetheriaRuntimeStarbridgeOperationSelection Admit(
            IEnumerable<AetheriaRuntimeDaemonCommandDocument>? operations,
            string gameMode,
            string modePolicyId,
            string sessionId,
            string runId,
            long currentFrameId,
            AetheriaRuntimeVerseAuthorityPolicyDocument? policy,
            IEnumerable<AetheriaRuntimeStarbridgePlayerSeatDocument>? seats,
            AetheriaRuntimeRunCheckpointCommit? run,
            string hostRuntimeId)
        {
            var ordered = (operations ?? Enumerable.Empty<AetheriaRuntimeDaemonCommandDocument>())
                .Where(value => value != null && !string.IsNullOrWhiteSpace(value.CommandId))
                .OrderBy(value => value.IssuedAtUtc ?? "", StringComparer.Ordinal)
                .ThenBy(value => value.CommandId, StringComparer.Ordinal)
                .ToArray();
            if (!IsPilotInputActive(
                    gameMode,
                    modePolicyId,
                    policy,
                    hostRuntimeId))
                return new AetheriaRuntimeStarbridgeOperationSelection(
                    Array.Empty<AetheriaRuntimeDaemonCommandDocument>(),
                    ordered.Select(value => value.CommandId).ToArray());

            var activeSeats = (seats ?? Enumerable.Empty<AetheriaRuntimeStarbridgePlayerSeatDocument>())
                .Where(value => value != null &&
                    string.Equals(value.SessionId, sessionId ?? "", StringComparison.Ordinal) &&
                    string.Equals(value.RunId, runId ?? "", StringComparison.Ordinal) &&
                    string.Equals(value.ConnectionState, AetheriaRuntimeStarbridgePlayerSeatConnectionStates.Connected, StringComparison.Ordinal))
                .ToArray();
            var eligible = new List<(AetheriaRuntimeDaemonCommandDocument Command, bool Pilot, string Slot)>();
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var command in ordered)
            {
                var proposer = string.IsNullOrWhiteSpace(command.AuthorRuntimeId)
                    ? command.ClientId ?? ""
                    : command.AuthorRuntimeId;
                if (!string.IsNullOrWhiteSpace(command.ClientId) &&
                    !string.Equals(command.ClientId, proposer, StringComparison.Ordinal))
                {
                    rejected.Add(command.CommandId);
                    continue;
                }
                var subject = AetheriaRuntimeAuthorityRouter.ResolveSubjectKey(command);
                var claim = AetheriaRuntimeAuthorityRouter.ResolveClaimKind(command.Kind);
                var slot = OperationSlotKey(sessionId, currentFrameId, subject, claim);
                if (!string.Equals(command.SessionId, sessionId ?? "", StringComparison.Ordinal) ||
                    command.ObservedFrameId != currentFrameId)
                {
                    rejected.Add(command.CommandId);
                    continue;
                }
                if (string.Equals(proposer, hostRuntimeId ?? "", StringComparison.Ordinal))
                {
                    eligible.Add((command, false, slot));
                    continue;
                }

                if (!TryResolvePilotSeat(activeSeats, sessionId, runId, proposer, run, out var pilotSeat, out var controlledEntityKey) ||
                    !string.Equals(controlledEntityKey, command.ActorEntityKey ?? "", StringComparison.Ordinal) ||
                    !(pilotSeat.ClaimKinds ?? Array.Empty<string>()).Contains(
                        AetheriaRuntimeClaimKinds.Movement, StringComparer.Ordinal) ||
                    command.Kind != AetheriaRuntimeDaemonCommandKinds.SetMoveVector ||
                    !string.Equals(claim, AetheriaRuntimeClaimKinds.Movement, StringComparison.Ordinal))
                {
                    rejected.Add(command.CommandId);
                    continue;
                }
                eligible.Add((command, true, slot));
            }

            var selected = new List<AetheriaRuntimeDaemonCommandDocument>();
            foreach (var slot in eligible.GroupBy(value => value.Slot, StringComparer.Ordinal))
            {
                var pilots = slot.Where(value => value.Pilot).ToArray();
                var commanders = slot.Where(value => !value.Pilot).ToArray();
                if (pilots.Length > 1 || (pilots.Length == 0 && commanders.Length != 1))
                {
                    foreach (var ambiguous in slot)
                        rejected.Add(ambiguous.Command.CommandId);
                    continue;
                }
                var winner = pilots.Length == 1 ? pilots[0] : commanders[0];
                selected.Add(winner.Command);
                foreach (var operation in slot)
                    if (!ReferenceEquals(operation.Command, winner.Command))
                        rejected.Add(operation.Command.CommandId);
            }

            return new AetheriaRuntimeStarbridgeOperationSelection(
                selected.OrderBy(value => value.IssuedAtUtc ?? "", StringComparer.Ordinal)
                    .ThenBy(value => value.CommandId, StringComparer.Ordinal)
                    .ToArray(),
                rejected.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        }
    }
}
