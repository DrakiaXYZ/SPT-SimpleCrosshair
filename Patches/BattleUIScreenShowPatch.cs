using System;
using System.Reflection;
using SPT.Reflection.Patching;
using EFT;
using EFT.UI;
using HarmonyLib;

namespace SimpleCrosshair.Patches
{
    internal class BattleUIScreenShowPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(EftBattleUIScreen),
                                      nameof(EftBattleUIScreen.Show),
                                      new Type[] { typeof(GamePlayerOwner) });
        }

        [PatchPostfix]
        public static void PatchPostfix(EftBattleUIScreen __instance)
        {
            // Don't attach if we're in the hideout, because OnRegisterPlayer isn't called in the hideout so other things don't work right
            if (!GameUtils.IsGameActive) return;

            Plugin.Instance.TryAttachToBattleUIScreen(__instance);
        }
    }
}
