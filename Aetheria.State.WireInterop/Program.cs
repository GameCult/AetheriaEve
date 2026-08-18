using GameCult.Aetheria.State.Verse;
using MessagePack;

if (args.Length == 1 && args[0] == "emit")
{
    var command = AetheriaRuntimeDaemonCommandDocument.Create(
        AetheriaRuntimeDaemonCommandKinds.SetMoveVector,
        "wire-csharp",
        "local",
        41,
        "entity:wire-csharp");
    command.CommandId = "wire-csharp-command";
    command.IssuedAtUtc = "2026-08-18T12:00:00.0000000Z";
    command.DirectionX = 0.25;
    command.DirectionY = -0.5;
    command.ScalarValue = 0.75;
    Console.WriteLine(Convert.ToBase64String(MessagePackSerializer.Serialize(command)));
    return;
}

if (args.Length == 2 && args[0] is "verify-csharp" or "verify-typescript")
{
    var bytes = Convert.FromBase64String(args[1]);
    var command = MessagePackSerializer.Deserialize<AetheriaRuntimeDaemonCommandDocument>(bytes);

    if (args[0] == "verify-csharp")
    {
        Require(command.CommandId == "wire-csharp-command", "C# command id");
        Require(command.ClientId == "wire-csharp", "C# client id");
        Require(command.ObservedFrameId == 41, "C# frame id");
        Require(command.ActorEntityKey == "entity:wire-csharp", "C# actor");
        Require(command.DirectionX == 0.25 && command.DirectionY == -0.5, "C# direction");
    }
    else
    {
        Require(command.CommandId == "wire-typescript-command", "TypeScript command id");
        Require(command.ClientId == "wire-typescript", "TypeScript client id");
        Require(command.SessionId == "local", "TypeScript session id");
        Require(command.ObservedFrameId == 42, "TypeScript frame id");
        Require(command.Kind == AetheriaRuntimeDaemonCommandKinds.SetMoveVector, "TypeScript command kind");
        Require(command.ActorEntityKey == "entity:wire-typescript", "TypeScript actor");
        Require(command.DirectionX == -0.125 && command.DirectionY == 0.625, "TypeScript direction");
        Require(command.ScalarValue == 0.875, "TypeScript scalar");
        Require(command.AuthorRuntimeId == "wire-typescript", "TypeScript author runtime");
        Require(command.SubjectKey == "entity:wire-typescript", "TypeScript authority subject");
        Require(command.ClaimKind == AetheriaRuntimeClaimKinds.Movement, "TypeScript authority claim kind");
    }

    Console.WriteLine(Convert.ToBase64String(MessagePackSerializer.Serialize(command)));
    return;
}

Console.Error.WriteLine("Usage: Aetheria.State.WireInterop emit | verify-csharp <base64> | verify-typescript <base64>");
Environment.ExitCode = 2;

static void Require(bool condition, string field)
{
    if (!condition)
        throw new InvalidDataException($"Wire interop mismatch: {field}.");
}
