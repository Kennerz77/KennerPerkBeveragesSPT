using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Unity 2022.3.43f1 editor helpers for Dr. Kennerz's Perk Beverages.
/// Menu: Perks > 1. Import perk machines, then Perks > 2. Build perk_beverages.bundle.
/// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod PerkBundleTools.RunAllBatch
/// </summary>
public static class PerkBundleTools
{
    private const string BundleName = "perk_beverages.bundle";
    private const string BuildDir = "AssetBundles";
    // Only this install. Do NOT point at D:\OTHER LAUNCHERS\BSTATE GAMES.
    private const string PluginDir = @"D:\OTHER LAUNCHERS\BUTTERSTATE\BepInEx\plugins\PerkBeverages";
    // The bottles are items: the server mod serves their bundles (bundles.json next to its DLL)
    private const string ServerModDir = @"D:\OTHER LAUNCHERS\BUTTERSTATE\SPT_Runtime\user\mods\PerkBeverages";
    private const string KeyFolder = "PerkBeverages";

    [MenuItem("Perks/2. Build perk_beverages.bundle")]
    public static void BuildBundleMenu()
    {
        string dest = BuildBundle();
        if (dest != null)
            EditorUtility.RevealInFinder(dest);
    }

    public static string BuildBundle()
    {
        string[] prefabs = PerkImporter.Perks.Select(PerkImporter.PrefabPath).ToArray();
        string missing = prefabs.FirstOrDefault(p => AssetDatabase.LoadAssetAtPath<GameObject>(p) == null);
        if (missing != null)
        {
            Debug.LogError($"[Perks] No prefab at {missing}. Run step 1 first.");
            return null;
        }

        // Every sound too, so the plugin can load any of them by name
        string[] sounds = Directory.GetFiles("Assets/Perks/Audio", "*.wav").Select(p => p.Replace('\\', '/')).ToArray();

        Directory.CreateDirectory(BuildDir);
        var builds = new[]
        {
            new AssetBundleBuild { assetBundleName = BundleName, assetNames = prefabs.Concat(sounds).ToArray() }
        };

        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            BuildDir, builds, BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            Debug.LogError("[Perks] Bundle build failed; see console.");
            return null;
        }

        Directory.CreateDirectory(PluginDir);
        string dest = Path.Combine(PluginDir, BundleName);
        File.Copy(Path.Combine(BuildDir, BundleName), dest, true);
        Debug.Log($"[Perks] Built and copied bundle -> {dest}");
        return BuildBottles() ? dest : null;
    }

    /// <summary>
    /// One bundle per bottle prefab (EFT finds the prefab by the bundle's file name), each self-contained, into the
    /// server mod's bundles folder, listed in its bundles.json.
    /// </summary>
    public static bool BuildBottles()
    {
        var bundles = PerkBottle.Editor.BottleImporter.Bottles
            .SelectMany(b => new[] { PerkBottle.Editor.BottleImporter.ContainerName(b), PerkBottle.Editor.BottleImporter.LootName(b) })
            .Select(n => (bundle: n + ".bundle", asset: PerkBottle.Editor.BottleImporter.PrefabPath(n))).ToArray();
        string missing = bundles.Select(b => b.asset).FirstOrDefault(p => AssetDatabase.LoadAssetAtPath<GameObject>(p) == null);
        if (missing != null)
        {
            Debug.LogError($"[Perks] No prefab at {missing}. Run step 3 first.");
            return false;
        }
        string buildDir = Path.Combine(BuildDir, "items");
        Directory.CreateDirectory(buildDir);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(buildDir,
            bundles.Select(b => new AssetBundleBuild { assetBundleName = b.bundle, assetNames = new[] { b.asset } }).ToArray(),
            BuildAssetBundleOptions.ChunkBasedCompression, BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            Debug.LogError("[Perks] Bottle bundle build failed; see console.");
            return false;
        }
        string dest = Path.Combine(ServerModDir, "bundles", KeyFolder);
        Directory.CreateDirectory(dest);
        foreach (var (bundle, _) in bundles)
        {
            File.Copy(Path.Combine(buildDir, bundle), Path.Combine(dest, bundle), true);
            Debug.Log($"[Perks] {bundle}: {new FileInfo(Path.Combine(dest, bundle)).Length / 1024} KB");
        }
        string entries = string.Join(",\n", bundles.Select(b => $"    {{ \"key\": \"{KeyFolder}/{b.bundle}\", \"dependencyKeys\": [] }}"));
        File.WriteAllText(Path.Combine(ServerModDir, "bundles.json"), "{\n  \"manifest\": [\n" + entries + "\n  ]\n}\n");
        Debug.Log($"[Perks] Bottle bundles -> {dest}");
        return true;
    }

    public static void RunAllBatch()
    {
        try
        {
            PerkImporter.ImportAll();
            PerkBottle.Editor.BottleImporter.ImportAll();
            if (BuildBundle() == null)
                EditorApplication.Exit(1);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }
}
