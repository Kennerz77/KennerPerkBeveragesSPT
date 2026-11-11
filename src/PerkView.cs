using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using EFT.Animations;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace PerkBeverages
{
    /// <summary>
    /// The drink in first person. EFT's procedural animation layers sway, look and recoil rotation onto
    /// Weapon_root_anim's current local rotation every frame, trusting the item's animation to have put it back to rest
    /// first. On our rebuilt rig that didn't happen, so the rotations piled up and the arms swung round (the Ray Gun's
    /// issue, fixed the same way). Our clips never rotate Weapon_root_anim (the bottle moves through the weapon bone
    /// below it), so it's reset to rest here, moved by the F12 "Drink view" offsets in the camera's frame. Only the perk
    /// bottles' rigs are touched.
    /// </summary>
    internal static class PerkView
    {
        private const string RigPrefix = "item_perk_bottle_";

        // Rest local positions as the clips set them
        private static readonly Dictionary<Transform, Vector3> Rest = new Dictionary<Transform, Vector3>();

        public static void Enable(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(PerkView).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string method in new[] { "ApplyComplexRotation", "ApplySimpleRotation" })
                harmony.Patch(AccessTools.Method(typeof(ProceduralWeaponAnimation), method), prefix);

            // The drink's length: the game reads it from the template as whole seconds (IHealthEffectsComponentTemplate.UseTime
            // returns the int foodUseTime); our bottles take the F12 time instead, in fractions
            InterfaceMapping map = typeof(FoodDrinkTemplate).GetInterfaceMap(typeof(IHealthEffectsComponentTemplate));
            int i = System.Array.FindIndex(map.InterfaceMethods, m => m.Name == "get_UseTime");
            if (i >= 0)
                harmony.Patch(map.TargetMethods[i],
                    postfix: new HarmonyMethod(typeof(PerkView).GetMethod(nameof(UseTimePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            else
                PerkPlugin.Log.LogWarning("The drink's use time wasn't found: the bottle keeps the server's whole seconds");

            // EFT holds every drink for its global start delay (2 s) before the drink's own time even begins: none for ours
            MethodInfo doMed = AccessTools.Method(typeof(ActiveHealthController), nameof(ActiveHealthController.DoMedEffect));
            if (doMed != null)
                harmony.Patch(doMed,
                    new HarmonyMethod(typeof(PerkView).GetMethod(nameof(DoMedEffectPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                    new HarmonyMethod(typeof(PerkView).GetMethod(nameof(DoMedEffectPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        /// <summary>The game's MedEffect settings' DrinkStartDelay (a static of BaseHealthController&lt;Effect&gt;).</summary>
        private static Traverse DrinkStartDelay()
        {
            System.Type generic = AccessTools.TypeByName("EFT.HealthSystem.BaseHealthController`1");
            System.Type effect = AccessTools.Inner(typeof(ActiveHealthController), "Effect");
            if (generic == null || effect == null)
                return null;
            Traverse delay = Traverse.Create(generic.MakeGenericType(effect)).Field("_settings").Field("Effects").Field("MedEffect").Field("DrinkStartDelay");
            return delay.FieldExists() ? delay : null;
        }

        private static void DoMedEffectPrefix(Item item, out float? __state)
        {
            __state = null;
            string template = item?.TemplateId.ToString();
            if (template == null || PerkDef.All.All(p => p.Bottle != template))
                return;
            try
            {
                Traverse delay = DrinkStartDelay();
                if (delay == null)
                    return;
                __state = delay.GetValue<float>();
                delay.SetValue(0f);
            }
            catch (System.Exception e)
            {
                PerkPlugin.Log.LogWarning($"Couldn't skip the drink's start delay: {e.Message}");
            }
        }

        private static void DoMedEffectPostfix(float? __state)
        {
            if (__state.HasValue)
                DrinkStartDelay()?.SetValue(__state.Value);
        }

        private static void UseTimePostfix(FoodDrinkTemplate __instance, ref float __result)
        {
            string id = __instance._id.ToString();
            foreach (PerkDef perk in PerkDef.All)
            {
                if (perk.Bottle == id)
                {
                    __result = PerkPlugin.DrinkTime.Value;
                    return;
                }
            }
        }

        private static void Prefix(ProceduralWeaponAnimation __instance)
        {
            Transform rootAnim = __instance.HandsContainer?.WeaponRootAnim;
            if (rootAnim == null || !rootAnim.root.name.StartsWith(RigPrefix))
                return;
            if (!Rest.TryGetValue(rootAnim, out Vector3 rest))
                Rest[rootAnim] = rest = rootAnim.localPosition;
            rootAnim.localRotation = Quaternion.identity;

            Vector3 offset = new Vector3(PerkPlugin.DrinkRight.Value, PerkPlugin.DrinkUp.Value, PerkPlugin.DrinkForward.Value);
            Camera camera = PerkInteraction.ActiveCamera;
            Transform parent = rootAnim.parent;
            rootAnim.localPosition = rest + (camera != null && parent != null
                ? parent.InverseTransformDirection(camera.transform.rotation * offset)
                : offset);

            // The bottle alone within the grip: its model root (tag_weapon under the animated weapon bone) never moves
            // in the clips, so it can be shifted and sized here
            Transform bottle = rootAnim.Find("weapon/tag_weapon");
            if (bottle == null)
                return;
            if (!BottleRest.TryGetValue(bottle, out var built))
                BottleRest[bottle] = built = (bottle.localPosition, bottle.localScale);
            Vector3 inHand = new Vector3(PerkPlugin.InHandRight.Value, PerkPlugin.InHandUp.Value, PerkPlugin.InHandForward.Value);
            bottle.localPosition = built.pos + (camera != null && bottle.parent != null
                ? bottle.parent.InverseTransformDirection(camera.transform.rotation * inHand)
                : inHand);
            bottle.localScale = built.scale * PerkPlugin.InHandSize.Value;
        }

        // The bottle model's position and size as built
        private static readonly Dictionary<Transform, (Vector3 pos, Vector3 scale)> BottleRest = new Dictionary<Transform, (Vector3, Vector3)>();
    }
}
