using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.Hideout;
using EFT.InventoryLogic;
using SPT.Reflection.Utils;
using UnityEngine;
using Random = UnityEngine.Random;

namespace PerkBeverages
{
    /// <summary>
    /// One perk machine in the hideout. With the hideout's power on it runs: the sign and its lights glow, it hums and
    /// now and then plays its jingle. When the power comes on it starts up as in the original (the start-up sound, the lights
    /// flickering to life). Without power it's dark and sparks now and then.
    /// </summary>
    internal class PerkMachine : MonoBehaviour
    {
        private const string CollisionLayer = "LowPolyCollider";
        private const float InteractDistance = 2.5f;
        private const float MaxDistance = 15f;
        private const float JingleMinInterval = 90f, JingleMaxInterval = 240f;

        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        private enum State { Unknown, Unpowered, Powered }

        public PerkDef Perk { get; private set; }

        private State _state;
        private float _nextStateCheck;
        private bool _starting;
        private readonly List<(Material material, Color emission)> _glowing = new List<(Material, Color)>();
        private Light[] _lights = new Light[0];
        private float[] _lightIntensities = new float[0];
        private AudioSource _jingle, _sting, _hum, _powerOn, _spark;
        private float _nextJingle, _nextSpark;
        private readonly PerkInteraction _menu = new PerkInteraction();

        public void Init(PerkDef perk, (Vector3 position, Quaternion rotation)? placed)
        {
            Perk = perk;
            name = $"Perk_{perk.Id} (PerkBeverages)";
            transform.localScale = Vector3.one * perk.Scale;
            transform.SetPositionAndRotation(placed?.position ?? perk.Position, placed?.rotation ?? Quaternion.Euler(perk.Rotation));

            FixShaders();
            SetupColliders();
            SetupGlow();
            SetupAudio();
            SetLit(false);

            Vector3 p = transform.position;
            PerkPlugin.Log.LogInfo($"Spawned {perk.Name} at ({p.x:F3}, {p.y:F3}, {p.z:F3}).");
        }

        private void OnDestroy() => _menu.Reset();

        private void Update()
        {
            UpdateState();
            if (PerkPlugin.Owner != null)
                _menu.Tick(PerkPlugin.Owner, transform, InteractDistance, Options);
            if (_state == State.Powered)
                UpdateJingle();
            else if (_state == State.Unpowered)
                UpdateSparks();
        }

        // ---------- Power ----------

        private static bool PowerOn
        {
            get
            {
                if (PerkPlugin.Power.Value != PerkPlugin.PowerMode.FollowHideout)
                    return PerkPlugin.Power.Value == PerkPlugin.PowerMode.On;
                try
                {
                    return !Singleton<HideoutRepresentation>.Instantiated
                           || Singleton<HideoutRepresentation>.Instance.EnergyController?.IsEnergyGenerationOn != false;
                }
                catch (Exception)
                {
                    return true;
                }
            }
        }

        private void UpdateState()
        {
            if (Time.unscaledTime < _nextStateCheck)
                return;
            _nextStateCheck = Time.unscaledTime + 0.25f;

            State want = PowerOn ? State.Powered : State.Unpowered;
            if (want == _state)
                return;
            State was = _state;
            _state = want;
            PerkPlugin.Log.LogInfo($"{Perk.Name}: {was} -> {want}.");

            StopAllCoroutines();
            _starting = false;
            if (want == State.Powered)
            {
                // The power coming on starts it up; walking in to a running machine, it's just on
                if (was == State.Unpowered)
                    StartCoroutine(PowerOnRoutine());
                else
                {
                    SetLit(true);
                    ScheduleJingle(20f, 60f);
                }
            }
            else
            {
                SetLit(false);
                _nextSpark = Time.time + Random.Range(3f, 10f);
            }
            ApplyAudioSettings();
        }

        /// <summary>F12 debug: the start-up again, as when the hideout's power comes on.</summary>
        public void ReplayPowerOn()
        {
            if (_state != State.Powered)
                return;
            StopAllCoroutines();
            StartCoroutine(PowerOnRoutine());
        }

        // Alternating on/off times (s) of the lights catching as the machine starts up, starting with on
        private static readonly float[] LifeFlicker = { 0.06f, 0.2f, 0.05f, 0.35f, 0.1f, 0.1f, 0.05f, 0.25f, 0.4f, 0.12f };

        private IEnumerator PowerOnRoutine()
        {
            _starting = true;
            SetLit(false);
            if (_jingle != null)
                _jingle.Stop();
            if (_powerOn != null && _powerOn.clip != null)
                _powerOn.Play();
            // The lights catch a moment into the start-up sound
            yield return new WaitForSeconds(0.6f);
            bool lit = false;
            foreach (float seconds in LifeFlicker)
            {
                lit = !lit;
                SetLit(lit);
                yield return new WaitForSeconds(seconds);
            }
            SetLit(true);
            _starting = false;
            ApplyAudioSettings(); // hum on
            // The first jingle comes soon after, so a visit actually hears it
            ScheduleJingle(15f, 45f);
        }

        /// <summary>The sign's glow and the lights on or off.</summary>
        private void SetLit(bool lit)
        {
            foreach (var (material, emission) in _glowing)
            {
                if (material != null)
                    material.SetColor(EmissionColor, lit ? emission : Color.black);
            }
            for (int i = 0; i < _lights.Length; i++)
            {
                if (_lights[i] == null)
                    continue;
                _lights[i].enabled = lit;
                _lights[i].intensity = _lightIntensities[i];
            }
        }

        // ---------- Jingle and sparks ----------

        private void ScheduleJingle(float min, float max) => _nextJingle = Time.time + Random.Range(min, max);

        private void UpdateJingle()
        {
            if (_starting || _jingle == null || _jingle.isPlaying || Time.time < _nextJingle)
                return;
            PlayJingle();
        }

        /// <summary>The machine's jingle now (also F12 debug); only while it has power.</summary>
        public void PlayJingle()
        {
            if (_state != State.Powered || _jingle == null || _jingle.clip == null)
                return;
            _jingle.Play();
            ScheduleJingle(JingleMinInterval + _jingle.clip.length, JingleMaxInterval + _jingle.clip.length);
        }

        /// <summary>No power: now and then a crackle and a blink or two of the lights.</summary>
        private void UpdateSparks()
        {
            if (Time.time < _nextSpark)
                return;
            _nextSpark = Time.time + Random.Range(8f, 25f);
            StartCoroutine(Spark());
        }

        private IEnumerator Spark()
        {
            AudioClip clip = PerkAssets.Clip("perk_broken_" + Random.Range(0, 5));
            if (_spark != null && clip != null && PerkPlugin.SparkVolume.Value > 0f)
                _spark.PlayOneShot(clip, PerkPlugin.SparkVolume.Value);
            int blinks = Random.Range(1, 4);
            for (int i = 0; i < blinks && _state == State.Unpowered; i++)
            {
                SetLit(true);
                yield return new WaitForSeconds(Random.Range(0.04f, 0.12f));
                SetLit(false);
                yield return new WaitForSeconds(Random.Range(0.05f, 0.25f));
            }
        }

        // ---------- Interaction ----------

        private IEnumerable<(string, Action)> Options()
        {
            if (_state != State.Powered)
                yield return ("No power", null);
            else if (_starting)
                yield return ("Starting up...", null);
            else if (PerkDrink.Drinking || _buying)
                yield return ("...", null);
            else if (PerkDrink.Has(Perk.Id))
                yield return ($"You have {Perk.Name}", null);
            else
                yield return ($"Buy {Perk.Name} ({Perk.Price:N0} \u20bd)", Buy);
        }

        // ---------- Buying ----------

        private bool _buying;

        private void Buy()
        {
            if (!_buying)
                PerkPlugin.Instance.StartCoroutine(BuyRoutine());
        }

        /// <summary>
        /// The original's buy at the machine: the cha-ching, the machine's sting and the bottle dropping out, then the drink in the
        /// player's head (open, gulps, the bottle smashed), and the burp with the blur as the perk's icon comes up.
        /// The hideout doesn't let the player drink, so no bottle goes in the hands here: the real bottle and the original's drink
        /// are for raids (Therapist's bottles).
        /// </summary>
        private IEnumerator BuyRoutine()
        {
            ClientBackendSession session = ClientAppUtils.GetMainApp()?.GetClientBackEndSession() as ClientBackendSession;
            if (session == null)
                yield break;
            _buying = true;
            try
            {
                bool done = false, paid = false;
                string error = null;
                session.SendOperationRightNow(new BuyOperation { Action = BuyAction, perk = Perk.Id, carry = false }, result =>
                {
                    done = true;
                    paid = result != null && !result.Failed;
                    error = result?.Error;
                });
                for (float until = Time.time + 10f; !done && Time.time < until; )
                    yield return null;
                if (!paid)
                {
                    PerkDrink.PlayInHead(PerkSounds.Loose("deny"), PerkPlugin.PurchaseVolume.Value);
                    PerkPlugin.Warn(!done ? "The machine got no answer from the server." : string.IsNullOrEmpty(error) ? "The purchase didn't go through." : error);
                    yield break;
                }

                PerkDrink.PlayInHead(PerkSounds.Loose("purchase_accept"), PerkPlugin.PurchaseVolume.Value);
                if (_jingle != null)
                    _jingle.Stop();
                if (_sting != null && _sting.clip != null)
                    _sting.Play();
                AudioClip dispense = PerkAssets.Clip("bottle_dispense");
                if (_spark != null && dispense != null)
                    _spark.PlayOneShot(dispense, PerkPlugin.DrinkVolume.Value);
                ScheduleJingle(JingleMinInterval, JingleMaxInterval);
                yield return PerkDrink.DrinkInHead(Perk);
            }
            finally
            {
                _buying = false;
            }
        }

        private const float HandOverTimeout = 6f;

        /// <summary>
        /// Empty hands again after a hand-over that never finished: what Hideout Uncensored does for stuck hands (the
        /// game's operations fast-forwarded, a fresh empty-hands controller, the process status cleared).
        /// </summary>
        private static void ResetHands(Player player)
        {
            try
            {
                player.FastForwardCurrentOperations();
            }
            catch (Exception e)
            {
                PerkPlugin.Log.LogWarning($"Fast-forward failed: {e.Message}");
            }
            try
            {
                if (!(player.HandsController is Player.EmptyHandsController))
                {
                    player.DestroyController();
                    Player.EmptyHandsController hands = Player.EmptyHandsController.CreateController<Player.EmptyHandsController>(player);
                    HarmonyLib.AccessTools.FieldRefAccess<Player, Player.AbstractHandsController>("_handsController")(player) = hands;
                    hands.Spawn(1f, () => { });
                }
                player.ProcessStatus = default;
            }
            catch (Exception e)
            {
                PerkPlugin.Log.LogWarning($"Resetting the hands failed: {e.Message}");
            }
        }

        /// <summary>This perk's bottles on the character.</summary>
        private IEnumerable<Item> Bottles(Player player) =>
            player.Inventory?.Equipment?.GetAllItems().Where(i => i.TemplateId.ToString() == Perk.Bottle) ?? Enumerable.Empty<Item>();

        private const string BuyAction = "PerkBuy";

        /// <summary>Item event body; field names match the server's PerkBuyRequest.</summary>
        private class BuyOperation
        {
            public string Action;
            public string perk;
            public bool carry;
        }

        // ---------- Setup ----------

        private void FixShaders()
        {
            // Only rebind bundled shaders that can't run here (pink); swapping working ones can lose keyword variants
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material mat in renderer.materials)
                {
                    if (mat == null || mat.shader == null || mat.shader.isSupported)
                        continue;
                    Shader gameShader = Shader.Find(mat.shader.name);
                    if (gameShader != null)
                        mat.shader = gameShader;
                }
            }
        }

        private void SetupColliders()
        {
            int layer = LayerMask.NameToLayer(CollisionLayer);
            if (layer < 0)
            {
                PerkPlugin.Log.LogWarning($"Layer '{CollisionLayer}' not found; colliders stay on Default.");
                return;
            }
            foreach (Collider c in GetComponentsInChildren<Collider>(true))
                c.gameObject.layer = layer;
        }

        private void SetupGlow()
        {
            // The sign's emission, remembered so the machine can go dark without power (per machine: these are copies)
            _glowing.Clear();
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material material in renderer.materials)
                {
                    if (material != null && material.HasProperty(EmissionColor) && material.GetColor(EmissionColor).maxColorComponent > 0.001f)
                        _glowing.Add((material, material.GetColor(EmissionColor)));
                }
            }
            _lights = GetComponentsInChildren<Light>(true);
            foreach (Light light in _lights)
                light.range *= Perk.Scale; // a light's range ignores the machine's scale
            _lightIntensities = _lights.Select(l => l.intensity).ToArray();
        }

        private void SetupAudio()
        {
            AudioSource[] sources = GetComponentsInChildren<AudioSource>(true);
            AudioSource Find(string n) => sources.FirstOrDefault(s => s.gameObject.name == n);
            _jingle = Find("Audio_Jingle");
            _sting = Find("Audio_Sting");
            _hum = Find("Audio_Hum");
            // Loaded by name too, in case the prefab's own clip reference didn't come through
            if (_jingle != null && _jingle.clip == null)
                _jingle.clip = PerkAssets.Clip(Perk.Id + "_jingle");
            if (_sting != null && _sting.clip == null)
                _sting.clip = PerkAssets.Clip(Perk.Id + "_sting");
            if (_hum != null && _hum.clip == null)
                _hum.clip = PerkAssets.Clip("perk_hum");

            _powerOn = AddSource("Audio_PowerOn", PerkAssets.Clip("perk_power_on"));
            _spark = AddSource("Audio_Spark", null);
            ApplyAudioSettings();
        }

        private AudioSource AddSource(string sourceName, AudioClip clip)
        {
            var go = new GameObject(sourceName);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, 2.5f, 0.5f);
            AudioSource source = go.AddComponent<AudioSource>();
            source.clip = clip;
            return source;
        }

        public void ApplyAudioSettings()
        {
            foreach (AudioSource source in new[] { _jingle, _sting, _hum, _powerOn, _spark })
            {
                if (source == null)
                    continue;
                source.playOnAwake = false;
                source.spatialBlend = 1f;
                source.rolloffMode = AudioRolloffMode.Linear;
                source.minDistance = 1f;
                source.maxDistance = MaxDistance;
                source.dopplerLevel = 0f;
                source.volume = source == _jingle ? PerkPlugin.JingleVolume.Value
                    : source == _sting ? PerkPlugin.StingVolume.Value
                    : source == _hum ? PerkPlugin.HumVolume.Value
                    : source == _powerOn ? PerkPlugin.PowerOnVolume.Value
                    : 1f; // sparks: their volume goes in PlayOneShot
            }

            bool running = _state == State.Powered && !_starting;
            if (_jingle != null)
            {
                _jingle.loop = false;
                if (_state != State.Powered && _jingle.isPlaying)
                    _jingle.Stop();
            }
            if (_hum != null)
            {
                _hum.loop = true;
                if (running && !_hum.isPlaying)
                    _hum.Play();
                else if (!running && _hum.isPlaying)
                    _hum.Stop();
            }
        }
    }
}
