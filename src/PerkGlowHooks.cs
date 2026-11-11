using System;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using HarmonyLib;

namespace PerkBeverages
{
    /// <summary>Puts the perk glow on inventory cells as they're made or updated (the Pack-a-Punch's two hooks).</summary>
    internal static class PerkGlowHooks
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ItemView), "NewItemView"),
                postfix: new HarmonyMethod(typeof(PerkGlowHooks).GetMethod(nameof(ItemViewPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(AccessTools.Method(typeof(GridItemView), nameof(GridItemView.UpdateInfo)),
                postfix: new HarmonyMethod(typeof(PerkGlowHooks).GetMethod(nameof(GridViewPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static void ItemViewPostfix(ItemView __instance)
        {
            try
            {
                PerkGlow.Attach(__instance);
            }
            catch (Exception e)
            {
                // never let the glow break the inventory
                PerkPlugin.Log.LogError($"Perk glow failed: {e}");
            }
        }

        // Cells keep their view when the game only updates them: keep the glow in step
        private static void GridViewPostfix(GridItemView __instance)
        {
            try
            {
                PerkGlow.Attach(__instance);
            }
            catch (Exception)
            {
                // a half-built cell; the next update fixes it
            }
        }
    }
}
