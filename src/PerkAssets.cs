using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PerkBeverages
{
    /// <summary>Loads perk_beverages.bundle once, in the background at startup, and hands out machines and sounds by name.</summary>
    internal static class PerkAssets
    {
        public const string BundleFileName = "perk_beverages.bundle";

        private static AssetBundle _bundle;
        private static bool _failed;
        private static readonly Dictionary<string, Object> Loaded = new Dictionary<string, Object>();
        private static readonly HashSet<string> Missing = new HashSet<string>();

        public static string BundlePath => Path.Combine(PerkPlugin.Dir, BundleFileName);

        public static bool Preloading { get; private set; }

        public static bool Ready => _bundle != null && !Preloading;

        public static IEnumerator PreloadAsync()
        {
            if (_bundle != null || _failed)
                yield break;
            if (!File.Exists(BundlePath))
            {
                PerkPlugin.Log.LogError($"Bundle not found at {BundlePath}");
                _failed = true;
                yield break;
            }

            Preloading = true;
            try
            {
                AssetBundleCreateRequest request = AssetBundle.LoadFromFileAsync(BundlePath);
                yield return request;
                _bundle = request.assetBundle;
                if (_bundle == null)
                {
                    PerkPlugin.Log.LogError($"Failed to load {BundleFileName}. Was it built with Unity 2022.3.43f1 for StandaloneWindows64?");
                    _failed = true;
                }
            }
            finally
            {
                Preloading = false;
            }
        }

        /// <summary>The machine prefab perk_&lt;id&gt;, or null.</summary>
        public static GameObject Machine(string id) => Load<GameObject>("perk_" + id);

        /// <summary>A sound from the bundle (its file name without .wav), or null.</summary>
        public static AudioClip Clip(string name) => Load<AudioClip>(name);

        private static T Load<T>(string name) where T : Object
        {
            if (!Ready || Missing.Contains(name))
                return null;
            if (Loaded.TryGetValue(name, out Object done) && done != null)
                return done as T;
            T asset = _bundle.LoadAsset<T>(name);
            if (asset == null)
            {
                Missing.Add(name);
                PerkPlugin.Log.LogError($"'{name}' not in bundle. Assets: {string.Join(", ", _bundle.GetAllAssetNames())}");
                return null;
            }
            Loaded[name] = asset;
            return asset;
        }
    }
}
