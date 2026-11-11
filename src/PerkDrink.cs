using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace PerkBeverages
{
    /// <summary>
    /// The perks the player has, and drinking one: the original's drink on the bottle's own hands model (its sounds ride the
    /// animation), with the screen going blurry while it goes down. Kept for the game session for now.
    /// </summary>
    internal static class PerkDrink
    {
        private static readonly HashSet<string> Owned = new HashSet<string>();

        public static bool Has(string perk) => Owned.Contains(perk);

        /// <summary>The perks held, in the order they were drunk (the HUD's order).</summary>
        public static readonly List<string> Order = new List<string>();

        public static bool Drinking { get; private set; }

        /// <summary>F12 debug "Remove my perks".</summary>

        public static void Clear()
        {
            Owned.Clear();
            Order.Clear();
        }

        private static AudioSource _head;

        /// <summary>A sound in the player's head (2D), at <paramref name="volume"/>.</summary>
        public static void PlayInHead(AudioClip clip, float volume)
        {
            if (clip == null || volume <= 0f)
                return;
            if (_head == null)
            {
                var go = new GameObject("perk_head_sounds");
                UnityEngine.Object.DontDestroyOnLoad(go);
                _head = go.AddComponent<AudioSource>();
                _head.spatialBlend = 0f;
                _head.playOnAwake = false;
            }
            _head.PlayOneShot(clip, volume);
        }

        // ---------- Drinking a bottle ----------

        // After the bottle is thrown: the gun coming back up, then the burp with the blur, then the perk is in
        // The blur is a quick blip (the user, 2026-10-31): in, a moment at full, out
        private const float DrawWait = 0.7f, BurpBlur = 0.15f;

        private static PerkDef _expected;
        private static bool _rearm;
        private static string _bottleId;
        private static PerkDef _drinking;
        private static float _since;

        /// <summary>The machine is handing over a bottle: after it's drunk, weapons come back out if they were.</summary>
        public static void ExpectDrink(PerkDef perk, bool rearm)
        {
            _expected = perk;
            _rearm = perk != null && rearm;
        }

        /// <summary>
        /// Once a frame: a perk bottle in the player's hands (from the machine, or drunk from the inventory) is watched;
        /// once it's gone (drunk, not put away), the original's order follows: gun back out, the burp with the blur, the perk.
        /// In raids too.
        /// </summary>
        public static void Watch()
        {
            // The hideout's owner outlives the visit as a destroyed object (its "?." still answers), so ask which it is
            Player player = PerkPlugin.InHideout ? PerkPlugin.Owner.HideoutPlayer
                : Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer as Player : null;
            Item held = player?.HandsController is Player.MedsController ? player.HandsController.Item : null;
            PerkDef perk = held == null ? null : PerkDef.All.FirstOrDefault(p => p.Bottle == held.TemplateId.ToString());

            if (perk != null && _drinking == null)
            {
                _drinking = perk;
                _bottleId = held.Id.ToString();
                _since = Time.time;
                Drinking = true;
            }
            if (_drinking == null)
                return;
            if (perk == _drinking && held.Id.ToString() == _bottleId)
                return;

            // Out of the hands: drunk if the bottle leaves the gear (the game takes it a moment after the hands change)
            if (_rearm && PerkPlugin.InHideout)
                PerkUncensored.SetArmed(true);
            PerkPlugin.Instance.StartCoroutine(ConfirmDrunk(_drinking, _bottleId, player));
            _drinking = null;
            _expected = null;
            _rearm = false;
            _bottleId = null;
        }

        /// <summary>
        /// The drink without a bottle in the hands (the machine in the hideout): the original's drink sounds at its notetrack
        /// times (frames 16, 29 and 57 at 30 fps), then the burp with the blur and the perk.
        /// </summary>
        public static IEnumerator DrinkInHead(PerkDef perk)
        {
            Drinking = true;
            float v = PerkPlugin.DrinkVolume.Value;
            // The bottle dropping out of the machine first
            yield return new WaitForSeconds(0.8f);
            yield return new WaitForSeconds(16f / 30f);
            PlayInHead(PerkAssets.Clip("bottle_open"), v);
            yield return new WaitForSeconds(13f / 30f);
            PlayInHead(PerkAssets.Clip("bottle_swallow"), v);
            yield return new WaitForSeconds(0.55f);
            PlayInHead(PerkAssets.Clip("bottle_swallow"), v);
            yield return new WaitForSeconds(28f / 30f - 0.55f);
            PlayInHead(PerkAssets.Clip("bottle_break"), v * 0.8f);
            PerkSplash.Smash(perk, PerkPlugin.InHideout ? PerkPlugin.Owner.HideoutPlayer : null);
            yield return AfterDrink(perk);
        }

        /// <summary>Up to <see cref="ConsumeWait"/> for the bottle to leave the gear: drunk, or only put away.</summary>
        private static IEnumerator ConfirmDrunk(PerkDef perk, string bottleId, Player player)
        {
            for (float until = Time.time + ConsumeWait; Time.time < until; )
            {
                if (player == null || player.Inventory?.Equipment?.GetAllItems().All(i => i.Id.ToString() != bottleId) != false)
                {
                    // The bottle smashed a moment ago, at the end of the throw
                    PerkSplash.Smash(perk, player);
                    yield return AfterDrink(perk);
                    yield break;
                }
                yield return null;
            }
            Log($"{perk.Name} was put away, not drunk.");
            Drinking = false;
        }

        private const float ConsumeWait = 2f;

        /// <summary>The gun comes back up, then the burp with the screen going blurry, then the perk (and its icon).</summary>
        private static IEnumerator AfterDrink(PerkDef perk)
        {
            try
            {
                yield return new WaitForSeconds(DrawWait);
                // The burp, the blur and the perk's icon together
                PlayInHead(PerkAssets.Clip("bottle_belch"), PerkPlugin.DrinkVolume.Value);
                PerkBlur.Begin();
                Owned.Add(perk.Id);
                if (!Order.Contains(perk.Id))
                    Order.Add(perk.Id);
                Log($"Drank {perk.Name}.");
                yield return new WaitForSeconds(BurpBlur);
                PerkBlur.End();
            }
            finally
            {
                Drinking = false;
            }
        }

        private static void Log(string message) => PerkPlugin.Log.LogInfo(message);
    }

    /// <summary>
    /// The short blur while a perk goes down: EFT's own camera effects (Colorful's double vision and radial blur, the
    /// ones its concussion uses), faded in and out. If the camera has neither, a soft dark wash over the screen.
    /// </summary>
    internal class PerkBlur : MonoBehaviour
    {
        private const float FadeIn = 0.08f, FadeOut = 0.25f;
        private const float DoubleVisionAmount = 1f, RadialAmount = 0.25f;
        private static readonly Vector2 Displace = new Vector2(0.7f, 0f);

        private static PerkBlur _instance;
        private float _level, _target;
        private Camera _camera;
        private readonly List<(Behaviour effect, bool wasEnabled, Dictionary<FieldInfo, object> saved)> _effects =
            new List<(Behaviour, bool, Dictionary<FieldInfo, object>)>();
        private static Texture2D _wash;

        public static void Begin()
        {
            if (_instance == null)
            {
                var go = new GameObject("perk_blur");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<PerkBlur>();
            }
            _instance.Hook();
            _instance._target = 1f;
        }

        public static void End()
        {
            if (_instance != null)
                _instance._target = 0f;
        }

        private void Hook()
        {
            Camera camera = PerkInteraction.ActiveCamera;
            if (camera == _camera && _effects.Count > 0)
                return;
            Release();
            _camera = camera;
            if (camera == null)
                return;
            foreach (Behaviour b in camera.GetComponents<Behaviour>())
            {
                string type = b.GetType().Name;
                if (type != "CC_DoubleVision" && type != "CC_RadialBlur")
                    continue;
                var saved = new Dictionary<FieldInfo, object>();
                foreach (FieldInfo f in b.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    saved[f] = f.GetValue(b);
                _effects.Add((b, b.enabled, saved));
                PerkPlugin.Log.LogInfo($"Drink blur uses {type} ({string.Join(", ", saved.Keys.Select(f => f.Name + ":" + f.FieldType.Name))}).");
            }
            if (_effects.Count == 0)
                PerkPlugin.Log.LogInfo("No camera blur effects found; the drink uses a screen wash.");
        }

        private void Update()
        {
            if (_level == _target)
                return;
            _level = Mathf.MoveTowards(_level, _target, Time.deltaTime / (_target > _level ? FadeIn : FadeOut));
            float eased = _level * _level * (3f - 2f * _level);
            foreach (var (effect, _, _) in _effects)
            {
                if (effect == null)
                    continue;
                effect.enabled = true;
                bool radial = effect.GetType().Name == "CC_RadialBlur";
                Set(effect, radial ? RadialAmount * eased : DoubleVisionAmount * eased, "amount", "Amount");
                if (!radial)
                    Set(effect, Displace * eased, "displace", "Displace");
            }
            if (_level <= 0f)
                Release();
        }

        private static void Set(Behaviour effect, object value, params string[] names)
        {
            foreach (string name in names)
            {
                FieldInfo f = effect.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null && f.FieldType == value.GetType())
                {
                    f.SetValue(effect, value);
                    return;
                }
            }
        }

        /// <summary>The camera's effects back exactly as they were.</summary>
        private void Release()
        {
            foreach (var (effect, wasEnabled, saved) in _effects)
            {
                if (effect == null)
                    continue;
                foreach (var pair in saved)
                    pair.Key.SetValue(effect, pair.Value);
                effect.enabled = wasEnabled;
            }
            _effects.Clear();
            _camera = null;
        }

        private void OnGUI()
        {
            if (_level <= 0f || _effects.Count > 0)
                return;
            if (_wash == null)
            {
                _wash = new Texture2D(1, 1);
                _wash.SetPixel(0, 0, new Color(0.15f, 0.05f, 0.03f, 1f));
                _wash.Apply();
            }
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.35f * _level);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _wash);
            GUI.color = old;
        }
    }
}
