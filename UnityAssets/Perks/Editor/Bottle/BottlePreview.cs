using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace PerkBottle.Editor
{
    /// <summary>
    /// Renders the bottle hands rig with its clips sampled to logs/preview_*.png, so the retarget can be checked without
    /// the game. EFT's arm mesh isn't ours to ship, so the arms are drawn as sticks along the rig's bones.
    /// </summary>
    public static class BottlePreview
    {
        private static readonly (string clip, float t)[] Shots =
        {
            ("jugg_idle", 0f),
            ("jugg_drink_0_32", 0.25f),
            ("jugg_drink_0_32", 0.95f),
            ("jugg_drink_32_40_loop", 0.2f),
            ("jugg_drink_32_64", 0.3f),
            ("jugg_drink_32_64", 0.6f),
        };

        /// <summary>
        /// Each bottle's inventory picture (icons/bottle_&lt;id&gt;.png, which the plugin shows instead of the game's studio
        /// photo): the world bottle standing, turned a little to show its label, lit softly, on a clear background,
        /// in the 1x2 cell's tall shape.
        /// </summary>
        /// <summary>The bottle's turn for its picture, so the label faces out.</summary>
        public static float IconYaw = 110f;

        /// <summary>Test renders of the picture at several turns, to logs/icon_yaw_*.png.</summary>
        public static void RenderIconAngles()
        {
            foreach (float yaw in new[] { 20f, 110f, 290f })
            {
                IconYaw = yaw;
                RenderIcons();
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../icons"));
                File.Copy(Path.Combine(dir, "bottle_jugg.png"), Path.Combine(RgPaths.LogsDir, $"icon_yaw_{yaw:0}.png"), true);
            }
        }

        public static void RenderIcons()
        {
            string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "../../icons"));
            Directory.CreateDirectory(dir);
            var lightGo = new GameObject("icon_light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lightGo.transform.rotation = Quaternion.Euler(25f, -35f, 0f);
            var fillGo = new GameObject("icon_fill");
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.45f;
            fillGo.transform.rotation = Quaternion.Euler(10f, 150f, 0f);
            RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.48f);

            var camGo = new GameObject("icon_cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            cam.orthographic = true;
            cam.nearClipPlane = 0.01f;
            var rt = new RenderTexture(256, 512, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;
            foreach (BottleImporter.Bottle b in BottleImporter.Bottles)
            {
                GameObject go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(BottleImporter.PrefabPath(BottleImporter.LootName(b))));
                go.transform.rotation = Quaternion.Euler(0f, IconYaw, 0f);
                Bounds bounds = go.GetComponentsInChildren<MeshRenderer>().First().bounds;
                cam.orthographicSize = bounds.extents.y * 1.04f;
                cam.transform.position = bounds.center + Vector3.forward * -1f;
                cam.transform.rotation = Quaternion.identity;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                string path = Path.Combine(dir, $"bottle_{b.Id}.png");
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
                Object.DestroyImmediate(go);
                Debug.Log($"[Perks] Icon {path}");
            }
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(lightGo);
            Object.DestroyImmediate(fillGo);
        }

        public static void RenderBatch()
        {
            RenderIcons();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BottleImporter.PrefabPath(BottleImporter.RigName(BottleImporter.Bottles[0])));
            GameObject go = Object.Instantiate(prefab);
            AddSticks(go.transform);

            var lightGo = new GameObject("light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 0.9f;
            lightGo.transform.rotation = Quaternion.Euler(35f, -30f, 0f);
            RenderSettings.ambientLight = new Color(0.35f, 0.35f, 0.38f);

            var camGo = new GameObject("cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.15f);
            cam.nearClipPlane = 0.01f;
            var rt = new RenderTexture(960, 640, 24);
            cam.targetTexture = rt;
            Directory.CreateDirectory(RgPaths.LogsDir);

            Transform eye = go.GetComponentsInChildren<Transform>(true).First(t => t.name == "Camera_animated");
            foreach (var (pattern, time) in Shots)
            {
                string file = Directory.GetFiles(RgPaths.Clips, pattern.Replace("*", "") + (pattern.EndsWith("*") ? "*" : "") + ".anim")
                    .Concat(Directory.GetFiles(RgPaths.Clips, pattern + ".anim")).FirstOrDefault();
                if (file == null)
                {
                    Debug.LogWarning($"[Perks] Preview: no clip {pattern}");
                    continue;
                }
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(file.Replace('\\', '/'));
                clip.SampleAnimation(go, Mathf.Min(time, clip.length));
                string tag = Path.GetFileNameWithoutExtension(file);

                // First person: from EFT's camera bone (it looks down its local -Y with +Z up)
                cam.fieldOfView = 55f;
                cam.transform.position = eye.position;
                cam.transform.rotation = Quaternion.LookRotation(eye.rotation * Vector3.down, eye.rotation * Vector3.forward);
                Shoot(cam, rt, $"fp_{tag}");

                // From the right side, to judge arm reach and the gun's placement
                cam.fieldOfView = 40f;
                cam.transform.position = eye.position + new Vector3(1.6f, 0.1f, 0.35f);
                cam.transform.LookAt(eye.position + new Vector3(0f, -0.15f, 0.35f));
                Shoot(cam, rt, $"side_{tag}");
            }
            Object.DestroyImmediate(go);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(lightGo);
        }

        /// <summary>A thin box from each arm bone to each child, parented to the bone so it follows the pose.</summary>
        private static void AddSticks(Transform root)
        {
            var mat = new Material(Shader.Find("Standard")) { color = new Color(0.85f, 0.6f, 0.45f) };
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("Base Human")).ToList())
            {
                foreach (Transform child in t.Cast<Transform>().Where(c => c.name.StartsWith("Base Human")).ToList())
                {
                    float length = child.localPosition.magnitude;
                    if (length < 1e-4f)
                        continue;
                    var stick = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    Object.DestroyImmediate(stick.GetComponent<Collider>());
                    stick.GetComponent<Renderer>().sharedMaterial = mat;
                    stick.transform.SetParent(t, false);
                    stick.transform.localPosition = child.localPosition * 0.5f;
                    stick.transform.localRotation = Quaternion.FromToRotation(Vector3.forward, child.localPosition.normalized);
                    bool finger = t.name.Contains("Digit") || child.name.Contains("Digit");
                    float w = finger ? 0.012f : 0.035f;
                    stick.transform.localScale = new Vector3(w, w, length);
                }
            }
        }

        private static void Shoot(Camera cam, RenderTexture rt, string name)
        {
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes(Path.Combine(RgPaths.LogsDir, $"preview_{name}.png"), tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
        }
    }
}
