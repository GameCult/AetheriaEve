namespace GameCult.Aetheria.State.Verse
{
    public static class AetheriaRuntimeStateBoundary
    {
        public const string RuntimeDaemonCommandBoundaryFileSuffix = ".daemon.commands.cc";

        public static string GetDaemonCommandBoundaryPath(string stateFilePath)
        {
            return stateFilePath + RuntimeDaemonCommandBoundaryFileSuffix;
        }
    }
}
