using System;
using System.Reflection;
using HarmonyLib;

namespace PerkBeverages
{
    /// <summary>
    /// Weapons in the hideout come from Hideout Uncensored (as for the Pack-a-Punch): its session decides whether weapons
    /// are out ("Want") and moves the game there every frame, so the machine asks it to holster before handing over the
    /// bottle, and to draw again after. Without it nothing happens here.
    /// </summary>
    internal static class PerkUncensored
    {
        private static PropertyInfo _want;
        private static bool _looked;

        private static bool Ready()
        {
            if (!_looked)
            {
                _looked = true;
                Type session = AccessTools.TypeByName("HideoutUncensored.HideoutSession");
                _want = session == null ? null : AccessTools.Property(session, "Want");
            }
            return _want != null && _want.GetSetMethod(true) != null;
        }

        /// <summary>Hideout Uncensored will draw (true) or holster (false) as soon as the game allows.</summary>
        public static void SetArmed(bool armed)
        {
            try
            {
                if (Ready())
                    _want.GetSetMethod(true).Invoke(null, new object[] { armed });
            }
            catch (Exception e)
            {
                PerkPlugin.Log.LogWarning($"Hideout Uncensored: {e.GetBaseException().Message}");
            }
        }
    }
}
