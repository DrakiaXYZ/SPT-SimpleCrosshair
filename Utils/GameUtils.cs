using Comfort.Common;
using EFT;

namespace SimpleCrosshair
{
    public static class GameUtils
    {
        public static Player Player => Singleton<GameWorld>.Instance.MainPlayer;
        public static bool IsGameActive => Singleton<AbstractGame>.Instantiated && !(Singleton<AbstractGame>.Instance is HideoutGame);
    }
}