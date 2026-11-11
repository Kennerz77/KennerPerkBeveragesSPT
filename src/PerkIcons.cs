using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace PerkBeverages
{
    /// <summary>
    /// The bottles' inventory pictures: rendered at build time (icons\bottle_&lt;id&gt;.png beside the plugin) instead of
    /// the game's icon studio photo, each fitted into the sprite the studio made, so it sits in the cell like any icon.
    /// (As the Ray Gun's.)
    /// </summary>
    internal static class PerkIcons
    {
        private static readonly Dictionary<string, Texture2D> Images = new Dictionary<string, Texture2D>();

        // Bumped whenever the pictures change, so the game's disk cache of older ones is retired
        private const int Salt = 0x50420003;

        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ItemIconCreator), nameof(ItemIconCreator.RenderModel)),
                postfix: new HarmonyMethod(typeof(PerkIcons).GetMethod(nameof(RenderModelPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(AccessTools.Method(typeof(IconsHash), nameof(IconsHash.GetItemHash)),
                postfix: new HarmonyMethod(typeof(PerkIcons).GetMethod(nameof(IconHashPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        private static string ImageFor(Item item)
        {
            string template = item?.TemplateId.ToString();
            PerkDef perk = template == null ? null : PerkDef.All.FirstOrDefault(p => p.Bottle == template);
            return perk == null ? null : "bottle_" + perk.Id;
        }

        // Our own pictures under their own hash, so the game's disk cache of studio photos is never reused for them
        private static void IconHashPostfix(Item item, ref int __result)
        {
            if (ImageFor(item) != null)
                __result = __result * 31 + Salt;
        }

        private static void RenderModelPostfix(Item item, ref Task<IconCreatorBase<Item, ItemIcon>.RenderModelResult> __result)
        {
            string image = ImageFor(item);
            if (image != null && __result != null)
                __result = WithImage(__result, image);
        }

        private static async Task<IconCreatorBase<Item, ItemIcon>.RenderModelResult> WithImage(Task<IconCreatorBase<Item, ItemIcon>.RenderModelResult> task, string name)
        {
            var result = await task;
            try
            {
                Texture2D image = Load(name);
                if (result.sprite != null && image != null)
                    result.sprite = Fit(result.sprite, image);
            }
            catch (System.Exception e)
            {
                PerkPlugin.Log.LogError($"Bottle icon {name} failed, keeping the studio's: {e.Message}");
            }
            return result;
        }

        private static Texture2D Load(string name)
        {
            if (Images.TryGetValue(name, out Texture2D cached) && cached != null)
                return cached;
            string path = Path.Combine(PerkPlugin.Dir, "icons", name + ".png");
            if (!File.Exists(path))
            {
                PerkPlugin.Log.LogWarning($"Bottle icon missing: {path}");
                return null;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "perk_icon_" + name, wrapMode = TextureWrapMode.Clamp };
            tex.LoadImage(File.ReadAllBytes(path), false); // stays readable: Fit samples it on the CPU
            return Images[name] = tex;
        }

        /// <summary>
        /// The picture scaled to fit the studio's sprite (same size, pivot and pixels per unit), centred, keeping its
        /// shape. On the CPU: a GPU blit from the studio's callbacks picks up its render state and washed icons out.
        /// </summary>
        private static Sprite Fit(Sprite like, Texture2D image)
        {
            int w = Mathf.Max(1, Mathf.RoundToInt(like.rect.width)), h = Mathf.Max(1, Mathf.RoundToInt(like.rect.height));
            float scale = Mathf.Min((float)w / image.width, (float)h / image.height) * 0.92f;
            float iw = image.width * scale, ih = image.height * scale;
            float x0 = (w - iw) / 2f, y0 = (h - ih) / 2f;
            var pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float u = (x + 0.5f - x0) / iw, v = (y + 0.5f - y0) / ih;
                    pixels[y * w + x] = u < 0f || u > 1f || v < 0f || v > 1f ? Color.clear : image.GetPixelBilinear(u, v);
                }
            }
            var fitted = new Texture2D(w, h, TextureFormat.RGBA32, false) { name = image.name, wrapMode = TextureWrapMode.Clamp };
            fitted.SetPixels(pixels);
            fitted.Apply(false, false); // readable: the game reads icons back to cache them
            var pivot = new Vector2(like.pivot.x / like.rect.width, like.pivot.y / like.rect.height);
            return Sprite.Create(fitted, new Rect(0, 0, w, h), pivot, like.pixelsPerUnit);
        }
    }
}
