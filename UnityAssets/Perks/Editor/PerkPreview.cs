using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Renders each machine from the front (its +Z), the back and a corner to logs/preview_&lt;id&gt;_*.png, lit and dark, so
/// the import can be checked without opening the editor.
/// Batch: Unity.exe -batchmode -quit -projectPath ... -executeMethod PerkPreview.RenderBatch
/// </summary>
public static class PerkPreview
{
    public static void RenderBatch()
    {
        var lightGo = new GameObject("light");
        var light = lightGo.AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = 0.5f;
        lightGo.transform.rotation = Quaternion.Euler(40f, 150f, 0f);

        var camGo = new GameObject("cam");
        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
        cam.fieldOfView = 40f;
        var rt = new RenderTexture(768, 768, 24);
        cam.targetTexture = rt;
        Directory.CreateDirectory("../logs");

        foreach (PerkImporter.Perk perk in PerkImporter.Perks)
        {
            GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(PerkImporter.PrefabPath(perk)));
            Bounds bounds = go.GetComponentsInChildren<MeshRenderer>().First().bounds;
            float dist = bounds.extents.magnitude * 2.6f;

            RenderSettings.ambientLight = new Color(0.25f, 0.25f, 0.25f);
            Shoot(cam, rt, bounds, dist, new Vector3(0f, 0.15f, 1f), $"{perk.Id}_front");
            Shoot(cam, rt, bounds, dist, new Vector3(0.8f, 0.3f, 0.8f), $"{perk.Id}_corner");
            Shoot(cam, rt, bounds, dist, new Vector3(0f, 0.15f, -1f), $"{perk.Id}_back");

            // Dark room: only the machine's own glow and lights
            RenderSettings.ambientLight = new Color(0.03f, 0.03f, 0.03f);
            light.enabled = false;
            Shoot(cam, rt, bounds, dist, new Vector3(0.3f, 0.15f, 1f), $"{perk.Id}_dark");
            light.enabled = true;

            foreach (Light l in go.GetComponentsInChildren<Light>())
                Debug.Log($"[Perks] {perk.Id} light {l.name} at {l.transform.localPosition}");
            Object.DestroyImmediate(go);
        }
    }

    private static void Shoot(Camera cam, RenderTexture rt, Bounds b, float dist, Vector3 dir, string name)
    {
        cam.transform.position = b.center + dir.normalized * dist;
        cam.transform.LookAt(b.center);
        cam.Render();

        RenderTexture.active = rt;
        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes($"../logs/preview_{name}.png", tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
    }
}
