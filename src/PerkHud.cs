using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace PerkBeverages
{
    /// <summary>
    /// The perks held, as the original's perk icons in a row along the bottom left of the screen, in the order they were drunk.
    /// Only while the player is in first person (the cursor hidden): not over the inventory or menus. A new icon pops
    /// in slightly big and settles.
    /// </summary>
    internal class PerkHud : MonoBehaviour
    {
        // Gap between icons, as a share of the screen height (size and position are F12 settings)
        private const float Gap = 0.006f;
        private const float PopSeconds = 0.35f;

        private static readonly Dictionary<string, Texture2D> Icons = new Dictionary<string, Texture2D>();
        private readonly Dictionary<string, float> _shownAt = new Dictionary<string, float>();

        private static Texture2D Icon(string perk)
        {
            if (Icons.TryGetValue(perk, out Texture2D icon))
                return icon;
            string path = Path.Combine(PerkPlugin.Dir, "icons", perk + ".png");
            if (File.Exists(path))
            {
                icon = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "perk_icon_" + perk };
                ImageConversion.LoadImage(icon, File.ReadAllBytes(path), true);
                // Drawn far smaller than its 256 px: trilinear over the mip chain keeps it smooth and sharp
                icon.filterMode = FilterMode.Trilinear;
                icon.anisoLevel = 4;
                icon.wrapMode = TextureWrapMode.Clamp;
            }
            else
                PerkPlugin.Log.LogWarning($"Perk icon missing: {path}");
            return Icons[perk] = icon;
        }

        private static PropertyInfo _displaying;
        private static Object _configManager;
        private static bool _looked;

        /// <summary>The F12 menu (BepInEx Configuration Manager) is open: it shows the cursor, but the icons stay up to be tuned.</summary>
        private static bool ConfigMenuOpen
        {
            get
            {
                if (!_looked)
                {
                    _looked = true;
                    System.Type type = HarmonyLib.AccessTools.TypeByName("ConfigurationManager.ConfigurationManager");
                    _displaying = type == null ? null : HarmonyLib.AccessTools.Property(type, "DisplayingWindow");
                    _configManager = type == null ? null : FindObjectOfType(type);
                }
                try
                {
                    return _displaying != null && _configManager != null && (bool)_displaying.GetValue(_configManager);
                }
                catch (System.Exception)
                {
                    return false;
                }
            }
        }

        private void OnGUI()
        {
            if (Event.current.type != EventType.Repaint || PerkDrink.Order.Count == 0 || (Cursor.visible && !ConfigMenuOpen) || !PerkPlugin.HudShown)
                return;
            float size = Screen.height * PerkPlugin.HudSize.Value, gap = Screen.height * Gap;
            float x = Screen.width * PerkPlugin.HudLeft.Value, bottom = Screen.height * (1f - PerkPlugin.HudBottom.Value);
            foreach (string perk in PerkDrink.Order)
            {
                Texture2D icon = Icon(perk);
                if (!_shownAt.TryGetValue(perk, out float since))
                    _shownAt[perk] = since = Time.unscaledTime;
                float t = Mathf.Clamp01((Time.unscaledTime - since) / PopSeconds);
                float scale = Mathf.Lerp(1.6f, 1f, t * t);
                float s = size * scale;
                if (icon != null)
                    GUI.DrawTexture(new Rect(x + (size - s) / 2f, bottom - size + (size - s) / 2f, s, s), icon, ScaleMode.ScaleToFit, true);
                x += size + gap;
            }
        }
    }
}
