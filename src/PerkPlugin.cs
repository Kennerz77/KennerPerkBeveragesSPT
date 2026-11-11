using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.Communications;
using HarmonyLib;
using UnityEngine;

namespace PerkBeverages
{
    /// <summary>
    /// Dr. Kennerz's Perk Beverages: the original's Perk-a-Cola machines in the hideout. They come alive with the hideout's
    /// power (start-up sound, lights flickering on, the hum and now and then their jingle) and sit dark, sparking now
    /// and then, without it. A perk is bought at its machine and drunk on the spot; its icon then shows on the HUD.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.SPT.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class PerkPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.kennerz.perkbeverages"; // same GUID as the server mod (Forge rule)
        public const string PluginName = "Kenner-PerkBeverages";     // Forge rule: "Username-ModName"
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static PerkPlugin Instance;

        /// <summary>The plugin's folder (the bundle, sounds\).</summary>
        internal static readonly string Dir = Path.GetDirectoryName(typeof(PerkPlugin).Assembly.Location);

        /// <summary>Handed over by the HideoutPlayerOwner.Init patch; Unity's null check makes it go null once destroyed.</summary>
        internal static HideoutPlayerOwner Owner;

        internal static ConfigEntry<float> JingleVolume, StingVolume, HumVolume, PowerOnVolume, SparkVolume, DrinkVolume, PurchaseVolume;

        /// <summary>F12 debug: the machines follow the hideout's power, or are held off or on.</summary>
        internal enum PowerMode { FollowHideout, Off, On }
        internal static ConfigEntry<PowerMode> Power;
        internal static ConfigEntry<float> HudLeft, HudBottom, HudSize;
        internal static ConfigEntry<float> DrinkRight, DrinkUp, DrinkForward;
        internal static ConfigEntry<float> DrinkTime, InHandRight, InHandUp, InHandForward, InHandSize;
        private static ConfigEntry<bool> NudgeEnabled;
        private static ConfigEntry<string> NudgeTarget;
        private static ConfigEntry<string> ShowMachine;

        private const string AllMachines = "All machines";

        private readonly Dictionary<string, PerkMachine> _machines = new Dictionary<string, PerkMachine>();
        private bool _loading;
        private bool _spawnTried; // set out (or tried to) this visit; cleared on leaving or a dropdown change

        private void Awake()
        {
            // Release builds: F12 shows players only their own settings
            ReleaseMenu.Install(this, d => d.Section == "1. Volume" || d.Section == "2. HUD");
            Log = Logger;
            Instance = this;
            BindConfig();

            var harmony = new Harmony(PluginGuid);
            harmony.Patch(AccessTools.Method(typeof(HideoutPlayerOwner), nameof(HideoutPlayerOwner.Init)),
                postfix: new HarmonyMethod(typeof(PerkPlugin), nameof(OwnerInitPostfix)));
            try
            {
                PerkIcons.Enable(harmony);
                PerkView.Enable(harmony);
                PerkGlowHooks.Enable(harmony);
            }
            catch (Exception e)
            {
                Log.LogError($"Bottle icon patches failed: {e}");
            }

            gameObject.AddComponent<PerkHud>();
            StartCoroutine(PerkAssets.PreloadAsync());
            Log.LogInfo($"{PluginName} {PluginVersion} loaded.");
        }

        private static void OwnerInitPostfix(HideoutPlayerOwner __instance) => Owner = __instance;

        private void BindConfig()
        {
            const string audio = "1. Volume";
            JingleVolume = Config.Bind(audio, "Jingle", 0.051f, Volume("Each machine's jingle, played now and then while it has power."));
            StingVolume = Config.Bind(audio, "Sting", 0.246f, Volume("The short tune when you buy a perk."));
            HumVolume = Config.Bind(audio, "Hum", 0.051f, Volume("The running machines' hum."));
            PowerOnVolume = Config.Bind(audio, "Power-on", 0.198f, Volume("The machines starting up when the hideout's power comes on."));
            SparkVolume = Config.Bind(audio, "Sparks (no power)", 0.25f, Volume("The crackle of a machine sparking now and then while the power is off."));
            PurchaseVolume = Config.Bind(audio, "Purchase", 0.303f, Volume("The cha-ching when a perk is paid for, and the buzz when it isn't."));
            DrinkVolume = Config.Bind(audio, "Drinking", 0.445f, Volume("Opening the bottle, gulping it down, the burp and the bottle smashing."));

            // Placing machines, the HUD and the drink's fit: tuned in game (2026-10-31) and hidden from F12 since. TuningMenu
            // brings them back (for placing the next machines); their values are kept in the .cfg either way
            const string hud = "2. HUD";
            // The icons' place and size are always in F12 (the user nudges them per screen, 2026-11-07)
            var Shown = new ConfigurationManagerAttributes { Browsable = true };
            HudLeft = Config.Bind(hud, "Perk icons: left edge", 0.175f, Tuning(
                "Where the row of perk icons starts, as a share of the screen's width from the left.", new AcceptableValueRange<float>(0f, 0.9f), Shown));
            HudBottom = Config.Bind(hud, "Perk icons: bottom edge", 0.012f, Tuning(
                "How far the perk icons sit above the bottom of the screen, as a share of its height.", new AcceptableValueRange<float>(0f, 0.9f), Shown));
            HudSize = Config.Bind(hud, "Perk icons: size", 0.045f, Tuning(
                "Each perk icon's size, as a share of the screen's height.", new AcceptableValueRange<float>(0.02f, 0.12f), Shown));

            const string view = "3. Drink view";
            // Defaults: the user's fit in game (2026-10-31)
            DrinkRight = Config.Bind(view, "Bottle: right", 0.177f, Tuning(
                "Moves the drinking animation (arms and bottle) to the right (negative: left), in metres, as you look. Live while drinking.",
                new AcceptableValueRange<float>(-0.6f, 0.6f)));
            DrinkUp = Config.Bind(view, "Bottle: up", 0.019f, Tuning(
                "Moves the drinking animation up (negative: down), in metres. Live while drinking.", new AcceptableValueRange<float>(-0.6f, 0.6f)));
            DrinkForward = Config.Bind(view, "Bottle: forward", 0.017f, Tuning(
                "Moves the drinking animation away from you (negative: closer), in metres. Live while drinking.", new AcceptableValueRange<float>(-0.6f, 0.6f)));

            DrinkTime = Config.Bind(view, "Bottle: time at mouth", 0.092f, Tuning(
                "How long the drink lasts once the bottle is up, in seconds: the one swig, then the bottle is thrown. " +
                "Takes effect on the next drink.", new AcceptableValueRange<float>(0.05f, 3f)));
            InHandRight = Config.Bind(view, "Bottle in hand: right", -0.025f, Tuning(
                "Moves the bottle alone within the grip (the hand stays put), to the right (negative: left), in metres. Live while drinking.",
                new AcceptableValueRange<float>(-0.1f, 0.1f)));
            InHandUp = Config.Bind(view, "Bottle in hand: up", 0.017f, Tuning(
                "Moves the bottle alone within the grip, up (negative: down), in metres. Live while drinking.", new AcceptableValueRange<float>(-0.1f, 0.1f)));
            InHandForward = Config.Bind(view, "Bottle in hand: forward", 0f, Tuning(
                "Moves the bottle alone within the grip, away from you (negative: closer), in metres. Live while drinking.",
                new AcceptableValueRange<float>(-0.1f, 0.1f)));
            InHandSize = Config.Bind(view, "Bottle in hand: size", 1f, Tuning(
                "The bottle's size in the hand (1 = as built). Smaller if the fingers sink in, bigger if they don't reach round. Live while drinking.",
                new AcceptableValueRange<float>(0.6f, 1.4f)));

            const string debug = "9. Debug";
            foreach (PerkDef perk in PerkDef.All)
            {
                PerkDef p = perk;
                // BepInEx keys can't hold ' (Widow's Wine)
                Config.Bind(debug, $"Bring {p.Name.Replace("'", "")} to me", false, Tuning(
                    $"Moves the {p.Name} machine 2.5 m in front of you, facing you. Fine-tune it with Unity Explorer, then press " +
                    "\"Log machine positions\".", null,
                    new ConfigurationManagerAttributes { Browsable = TuningMenu, HideDefaultButton = true, CustomDrawer = _ => DrawButton("Bring it here", () => BringToPlayer(p)) }));
            }
            ShowMachine = Config.Bind(debug, "Machine shown", PerkDef.All[0].Name, new ConfigDescription(
                "Which perk machine stands in the hideout, for checking each model's look and sounds: pick one and the machine is swapped in place " +
                "(it keeps wherever you moved it). \"All machines\" puts every machine at its own saved spot.",
                new AcceptableValueList<string>(new[] { AllMachines }.Concat(PerkDef.All.Select(p => p.Name)).ToArray()),
                new ConfigurationManagerAttributes { Browsable = true }));
            NudgeEnabled = Config.Bind(debug, "Arrow-key placement", false, Tuning(
                "Moves the chosen machine with the keyboard: arrows forward / back / left / right (as you face), Shift + Up / Down " +
                "raises and lowers it, Alt + Left / Right turns it. A tap is one small step; hold to keep going. Turn it off when done."));
            NudgeTarget = Config.Bind(debug, "Machine to move", PerkDef.All[0].Name, Tuning(
                "Which machine the arrow keys move.", new AcceptableValueList<string>(PerkDef.All.Select(p => p.Name).ToArray())));
            Config.Bind(debug, "Log machine positions", false, Tuning(
                "Writes every machine's position, rotation and scale to BepInEx\\LogOutput.log (for saving them in the mod).", null,
                new ConfigurationManagerAttributes { Browsable = TuningMenu, HideDefaultButton = true, CustomDrawer = _ => DrawButton("Log positions", LogPositions) }));
            Power = Config.Bind(debug, "Machine power", PowerMode.FollowHideout, Tuning(
                "FollowHideout: the machines run while the hideout's generator does. Off / On: held off or on whatever the generator does " +
                "(switching from Off to On plays the start-up)."));
            Config.Bind(debug, "Power up", false, Tuning(
                "Plays the machines' start-up again (sound and lights flickering on), as when the power comes on.", null,
                new ConfigurationManagerAttributes { Browsable = TuningMenu, HideDefaultButton = true, CustomDrawer = _ => DrawButton("Power up", () => ForEach(m => m.ReplayPowerOn())) }));
            Config.Bind(debug, "Remove my perks", false, Tuning(
                "Takes away every perk you have, so they can be bought again.", null,
                new ConfigurationManagerAttributes { Browsable = TuningMenu, HideDefaultButton = true, CustomDrawer = _ => DrawButton("Remove perks", PerkDrink.Clear) }));
            Config.Bind(debug, "Play jingle", false, Tuning(
                "Plays each powered machine's jingle now.", null,
                new ConfigurationManagerAttributes { Browsable = TuningMenu, HideDefaultButton = true, CustomDrawer = _ => DrawButton("Jingle", () => ForEach(m => m.PlayJingle())) }));

            Config.SettingChanged += (_, args) =>
            {
                if (args.ChangedSetting.Definition.Section == audio)
                    ForEach(m => m.ApplyAudioSettings());
                else if (args.ChangedSetting == ShowMachine)
                    SwapMachine();
            };
        }

        private static ConfigDescription Volume(string description) =>
            new ConfigDescription(description, new AcceptableValueRange<float>(0f, 1f));

        /// <summary>F12 shows only the volumes; on, it also shows the placement, HUD, drink-fit and debug entries.</summary>
        private const bool TuningMenu = false;

        /// <summary>A tuning entry: hidden from F12 unless <see cref="TuningMenu"/> (attributes given keep their own Browsable).</summary>
        private static ConfigDescription Tuning(string description, AcceptableValueBase range = null, ConfigurationManagerAttributes attributes = null) =>
            new ConfigDescription(description, range, attributes ?? new ConfigurationManagerAttributes { Browsable = TuningMenu });

        /// <summary>The F12 menu (BepInEx Configuration Manager) draws entries by this class's field names.</summary>
        internal sealed class ConfigurationManagerAttributes
        {
            public bool? Browsable;
            public bool? HideDefaultButton;
            public Action<ConfigEntryBase> CustomDrawer;
        }

        private static void DrawButton(string label, Action action)
        {
            if (GUILayout.Button(label, GUILayout.ExpandWidth(true)))
                action();
        }

        private void ForEach(Action<PerkMachine> action)
        {
            foreach (PerkMachine machine in _machines.Values)
            {
                if (machine != null)
                    action(machine);
            }
        }

        // ---------- Hideout lifecycle ----------

        /// <summary>The perk HUD shows in the hideout and in raids.</summary>
        internal static bool HudShown => InHideout || (Singleton<GameWorld>.Instantiated && Singleton<GameWorld>.Instance.MainPlayer != null);

        internal static bool InHideout
        {
            get
            {
                HideoutPlayerOwner owner = Owner;
                return owner != null && owner.gameObject.activeInHierarchy && owner.HideoutPlayer != null;
            }
        }

        // The hideout's test machines (development only): perk machines are the zombies mode's, never in a player's hideout
        private const bool HideoutTestMachines = false;

        private void Update()
        {
            bool inHideout = InHideout;
            if (HideoutTestMachines && inHideout && _machines.Count == 0 && !_loading && !_spawnTried)
                SpawnAll();
            else if (!inHideout && _machines.Count > 0)
                DespawnAll();
            if (inHideout && NudgeEnabled.Value)
                Nudge();
            PerkDrink.Watch();
        }

        private void SpawnAll()
        {
            if (!PerkAssets.Ready)
                return;
            _loading = true;
            try
            {
                foreach (PerkDef perk in PerkDef.All.Where(p => Shown(p)))
                {
                    GameObject prefab = PerkAssets.Machine(perk.Id);
                    if (prefab == null)
                    {
                        Log.LogWarning($"No {perk.Name} machine in perk_beverages.bundle (built before it was added?).");
                        if (SingleMode)
                            Warn($"The {perk.Name} machine isn't in the bundle yet.");
                        continue;
                    }
                    GameObject go = Instantiate(prefab);
                    PerkMachine machine = go.AddComponent<PerkMachine>();
                    // One machine shown: it stands where the last one did (or at the first perk's saved spot)
                    (Vector3, Quaternion)? spot = Placed.TryGetValue(SpotKey(perk), out var at) ? at
                        : SingleMode ? (PerkDef.All[0].Position, Quaternion.Euler(PerkDef.All[0].Rotation)) : ((Vector3, Quaternion)?)null;
                    machine.Init(perk, spot);
                    _machines[perk.Id] = machine;
                }
                // Nothing to spawn (a bad bundle): don't try again every frame. Only for all machines: a single missing
                // one is just that perk, and the dropdown can pick another
                if (_machines.Count == 0 && !SingleMode)
                    enabled = false;
                _spawnTried = true;
            }
            finally
            {
                _loading = false;
            }
        }

        private static bool SingleMode => ShowMachine.Value != AllMachines;
        private static bool Shown(PerkDef perk) => !SingleMode || perk.Name == ShowMachine.Value;

        /// <summary>Where a machine's moved position is kept: one shared spot while a single machine is shown.</summary>
        private static string SpotKey(PerkDef perk) => SingleMode ? "shown" : perk.Id;

        /// <summary>The F12 dropdown changed: the machines are replaced (a single one takes the place of the last).</summary>
        private void SwapMachine()
        {
            _spawnTried = false;
            if (_machines.Count == 0)
                return;
            // The spot of the machine being replaced, to hand on to the next one
            PerkMachine last = _machines.Values.FirstOrDefault(m => m != null);
            if (last != null && SingleMode)
                Placed["shown"] = (last.transform.position, last.transform.rotation);
            ForEach(m => Destroy(m.gameObject));
            _machines.Clear();
            _spawnTried = false;
        }

        private void DespawnAll()
        {
            ForEach(m => Destroy(m.gameObject));
            _machines.Clear();
            _spawnTried = false;
            Placed.Clear();
            Log.LogInfo("Left the hideout; perk machines despawned.");
        }

        // ---------- Debug placement ----------

        // Where "Bring to me" put each machine, kept across respawns while in the hideout
        private static readonly Dictionary<string, (Vector3, Quaternion)> Placed = new Dictionary<string, (Vector3, Quaternion)>();

        private void BringToPlayer(PerkDef perk)
        {
            HideoutPlayer player = Owner?.HideoutPlayer;
            if (!_machines.TryGetValue(perk.Id, out PerkMachine machine) || machine == null || player == null)
            {
                Warn($"The {perk.Name} machine isn't in the hideout right now.");
                return;
            }
            Transform body = ((Component)player).transform;
            Camera camera = PerkInteraction.ActiveCamera;
            Vector3 look = camera != null ? camera.transform.forward : body.forward;
            Vector3 flat = Vector3.ProjectOnPlane(look, Vector3.up).normalized;
            Vector3 at = body.position + flat * 2.5f;
            // Its front (the sign and the slot) faces the player
            Quaternion rotation = Quaternion.LookRotation(-flat, Vector3.up);
            machine.transform.SetPositionAndRotation(at, rotation);
            Placed[SpotKey(perk)] = (at, rotation);
            Log.LogInfo($"{perk.Name} brought to ({at.x:F3}, {at.y:F3}, {at.z:F3}), rotation y {rotation.eulerAngles.y:F2}.");
            Notify($"{perk.Name} moved to ({at.x:F2}, {at.y:F2}, {at.z:F2}). Fine-tune it with Unity Explorer.");
        }

        // One tap: 1 cm or 1 degree. Held past the delay, a step every repeat interval
        private const float StepMetres = 0.01f, StepDegrees = 1f, RepeatDelay = 0.35f, RepeatInterval = 0.03f;
        private static readonly KeyCode[] NudgeKeys = { KeyCode.UpArrow, KeyCode.DownArrow, KeyCode.LeftArrow, KeyCode.RightArrow };
        private float _nextRepeat;

        /// <summary>F12 debug: arrow keys move the chosen machine, relative to where the player is looking.</summary>
        private void Nudge()
        {
            PerkDef perk = PerkDef.All.FirstOrDefault(p => p.Name == (SingleMode ? ShowMachine.Value : NudgeTarget.Value));
            if (perk == null || !_machines.TryGetValue(perk.Id, out PerkMachine machine) || machine == null)
                return;

            KeyCode key = NudgeKeys.FirstOrDefault(Input.GetKey);
            if (key == KeyCode.None)
                return;
            if (Input.GetKeyDown(key))
                _nextRepeat = Time.unscaledTime + RepeatDelay;
            else if (Time.unscaledTime >= _nextRepeat)
                _nextRepeat = Time.unscaledTime + RepeatInterval;
            else
                return;

            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            Transform t = machine.transform;
            if (alt)
            {
                if (key == KeyCode.LeftArrow) t.Rotate(Vector3.up, -StepDegrees, Space.World);
                else if (key == KeyCode.RightArrow) t.Rotate(Vector3.up, StepDegrees, Space.World);
                else return;
            }
            else if (shift)
            {
                if (key == KeyCode.UpArrow) t.position += Vector3.up * StepMetres;
                else if (key == KeyCode.DownArrow) t.position += Vector3.down * StepMetres;
                else return;
            }
            else
            {
                // Forward / right as the player faces, flat on the floor
                Camera camera = PerkInteraction.ActiveCamera;
                Transform view = camera != null ? camera.transform : ((Component)Owner.HideoutPlayer).transform;
                Vector3 forward = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, forward);
                Vector3 step = key == KeyCode.UpArrow ? forward : key == KeyCode.DownArrow ? -forward : key == KeyCode.RightArrow ? right : -right;
                t.position += step * StepMetres;
            }
            // Kept if the machine respawns this visit
            Placed[SpotKey(perk)] = (t.position, t.rotation);
        }

        private void LogPositions()
        {
            if (_machines.Count == 0)
            {
                Warn("No perk machines in the hideout right now.");
                return;
            }
            foreach (var pair in _machines.Where(p => p.Value != null))
            {
                Transform t = pair.Value.transform;
                Vector3 p = t.position, r = t.eulerAngles, s = t.localScale;
                Log.LogInfo($"[positions] {pair.Key}: position new Vector3({p.x:F4}f, {p.y:F4}f, {p.z:F4}f), " +
                            $"rotation new Vector3({r.x:F2}f, {r.y:F2}f, {r.z:F2}f), scale {s.x:F4}f / {s.y:F4}f / {s.z:F4}f");
            }
            Notify("Machine positions written to BepInEx\\LogOutput.log.");
        }

        internal static void Warn(string message) =>
            NotificationManager.DisplayWarningNotification(message, ENotificationDurationType.Default);

        internal static void Notify(string message) =>
            NotificationManager.DisplayMessageNotification(message, ENotificationDurationType.Long, ENotificationIconType.Default, null);
    }

    /// <summary>One perk: its machine in the bundle (perk_&lt;Id&gt;), its name and where it stands.</summary>
    internal class PerkDef
    {
        public string Id, Name;
        public Vector3 Position, Rotation;
        public float Scale;
        public int Price = 25_000; // shown in the prompt; the server's config.json has what it charges (keep them in step)
        public string Bottle;      // the bottle's template id (the server's PerkIds)
        public Color Glow;         // the bottle's inventory glow
        public Color Liquid;       // the drink's own colour (the splash where the bottle smashes)

        public static readonly PerkDef[] All =
        {
            // Placed in game 2026-10-31
            new PerkDef { Id = "jugg", Name = "Jugger-Nog", Bottle = "6a7f9e4b0c0a115000000b01", Glow = new Color(1f, 0.12f, 0.08f), Liquid = new Color(0.55f, 0.04f, 0.03f), Position = new Vector3(8.7282f, -0.0100f, 6.5424f), Rotation = new Vector3(0f, 182.54f, 0f), Scale = 0.35f },
            // Stood in a row beside Jugger-Nog for now, 1 m apart: move them in game (TuningMenu) and bake the positions here
            new PerkDef { Id = "speed", Name = "Speed Cola", Bottle = "6a7f9e4b0c0a115000000b02", Glow = new Color(0.2f, 1f, 0.3f), Liquid = new Color(0.1f, 0.5f, 0.12f), Position = new Vector3(7.7282f, -0.0100f, 6.5424f), Rotation = new Vector3(0f, 182.54f, 0f), Scale = 0.35f },
            new PerkDef { Id = "dtap", Name = "Double Tap Root Beer", Bottle = "6a7f9e4b0c0a115000000b03", Glow = new Color(1f, 0.6f, 0.05f), Liquid = new Color(0.7f, 0.38f, 0.05f), Position = new Vector3(6.7282f, -0.0100f, 6.5424f), Rotation = new Vector3(0f, 182.54f, 0f), Scale = 0.35f },
            new PerkDef { Id = "deadshot", Name = "Deadshot Daiquiri", Bottle = "6a7f9e4b0c0a115000000b04", Glow = new Color(0.8f, 0.85f, 0.8f), Liquid = new Color(0.45f, 0.45f, 0.42f), Position = new Vector3(5.7282f, -0.0100f, 6.5424f), Rotation = new Vector3(0f, 182.54f, 0f), Scale = 0.35f },
            new PerkDef { Id = "stamin", Name = "Stamin-Up", Bottle = "6a7f9e4b0c0a115000000b05", Glow = new Color(1f, 0.75f, 0.1f), Liquid = new Color(0.7f, 0.55f, 0.1f), Position = new Vector3(4.7282f, -0.0100f, 6.5424f), Rotation = new Vector3(0f, 182.54f, 0f), Scale = 0.35f },
            new PerkDef { Id = "revive", Name = "Quick Revive", Bottle = "6a7f9e4b0c0a115000000b06", Glow = new Color(0.2f, 0.75f, 1f), Liquid = new Color(0.1f, 0.22f, 0.75f), Position = new Vector3(3.7282f, -0.0100f, 6.5424f), Rotation = new Vector3(0f, 182.54f, 0f), Scale = 0.35f },
            new PerkDef { Id = "mule", Name = "Mule Kick", Bottle = "6a7f9e4b0c0a115000000b07", Glow = new Color(0.35f, 0.85f, 0.25f), Liquid = new Color(0.6f, 0.07f, 0.05f), Position = new Vector3(2.7282f, -0.0100f, 6.5424f), Rotation = new Vector3(0f, 182.54f, 0f), Scale = 0.35f },
            new PerkDef { Id = "widow", Name = "Widow's Wine", Bottle = "6a7f9e4b0c0a115000000b08", Glow = new Color(0.9f, 0.1f, 0.25f), Liquid = new Color(0.28f, 0.05f, 0.14f), Position = new Vector3(1.7282f, -0.0100f, 6.5424f), Rotation = new Vector3(0f, 182.54f, 0f), Scale = 0.35f },
            new PerkDef { Id = "phd", Name = "PHD Flopper", Bottle = "6a7f9e4b0c0a115000000b09", Glow = new Color(0.55f, 0.35f, 1f), Liquid = new Color(0.4f, 0.28f, 0.75f), Position = new Vector3(0.7282f, -0.0100f, 6.5424f), Rotation = new Vector3(0f, 182.54f, 0f), Scale = 0.35f },
        };
    }
}
