using System.Collections.Generic;
using System.IO;
using System.Linq;
using Perks.SEFormats;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds one prefab per perk machine (Assets/Perks/perk_&lt;id&gt;.prefab) straight from Greyhound's SEModel exports,
/// the same way the Pack-a-Punch machine is built: Standard (Specular setup) materials, a collider, the sign's glow
/// and lights, and 3D audio objects the plugin drives. The original's perk machines have no animations: "on" is their glow.
/// </summary>
public static class PerkImporter
{
    // The asset export folder on the author's PC: the KZ_EXPORT_ROOT user environment variable (kept out of the source)
    private static readonly string ExportRoot = System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("KZ_EXPORT_ROOT", System.EnvironmentVariableTarget.User) ?? "", @"xmodels");

    private const string Root = "Assets/Perks";
    private const string GeneratedDir = Root + "/Generated";
    private const string TextureDir = Root + "/Textures";
    private const string MaterialDir = Root + "/Materials";
    private const string AudioDir = Root + "/Audio";

    // The original game units are inches
    private const float Scale = 0.0254f;

    // The original game is right-handed Z-up; Unity is left-handed Y-up. The machine's front (sign, slot) is the original game -Y, mapped to
    // Unity +Z, as on the Pack-a-Punch
    private static Vector3 ToUnity(SEVec3 v) => new Vector3(-v.X, v.Z, -v.Y) * Scale;
    private static Vector3 ToUnityDir(SEVec3 v) => new Vector3(-v.X, v.Z, -v.Y);

    /// <summary>One perk machine: its export, its glow colour and its own jingle and sting.</summary>
    public class Perk
    {
        public string Id;           // perk_<Id>.prefab, <Id>_jingle.wav, <Id>_sting.wav
        public string Model;        // the xmodel folder under ExportRoot
        public Color Glow;          // the sign light and the coil
        public string Jingle, Sting;
        public string SignSuffix = "_sign"; // the lit round sign's material name ends with this
        // Its sign glow and sign/panel lights against the approved look (1): a near-white sign (Quick Revive) reads far
        // brighter at the same settings (the user, 2026-11-08: "quick revive is a bit bright")
        public float Bright = 1f;
        // Its layered the original materials (tinted paint, wood) baked in colour from GrayWolf's export into Perks/Baked/<Model>
        // (C:\Git\99_Tools\cast_to_se.py --keep-colour): Greyhound's export only has the untinted base textures
        public bool Baked;
    }

    public static readonly Perk[] Perks =
    {
        new Perk { Id = "jugg", Model = "p7_zm_vending_jugg", Glow = new Color(1f, 0.35f, 0.2f), Jingle = "jugg_jingle.wav", Sting = "jugg_sting.wav" },
        new Perk { Id = "speed", Model = "p7_zm_vending_sleight", Glow = new Color(0.2f, 1f, 0.3f), Jingle = "speed_jingle.wav", Sting = "speed_sting.wav" },
        new Perk { Id = "dtap", Model = "p7_zm_vending_doubletap2", Glow = new Color(1f, 0.6f, 0.05f), Jingle = "dtap_jingle.wav", Sting = "dtap_sting.wav" },
        new Perk { Id = "deadshot", Model = "p7_zm_vending_ads", Glow = new Color(0.8f, 0.85f, 0.8f), Jingle = "deadshot_jingle.wav", Sting = "deadshot_sting.wav", SignSuffix = "_logo" },
        new Perk { Id = "stamin", Model = "p7_zm_vending_marathon", Glow = new Color(1f, 0.75f, 0.1f), Jingle = "stamin_jingle.wav", Sting = "stamin_sting.wav", SignSuffix = "_light", Baked = true },
        new Perk { Id = "revive", Model = "p7_zm_vending_revive", Glow = new Color(0.2f, 0.75f, 1f), Jingle = "revive_jingle.wav", Sting = "revive_sting.wav", Bright = 0.55f },
        new Perk { Id = "mule", Model = "p7_zm_vending_three_gun", Glow = new Color(0.35f, 0.85f, 0.25f), Jingle = "mule_jingle.wav", Sting = "mule_sting.wav", SignSuffix = "_plastic", Baked = true },
        new Perk { Id = "widow", Model = "p7_zm_vending_widows_wine", Glow = new Color(0.9f, 0.1f, 0.25f), Jingle = "widow_jingle.wav", Sting = "widow_sting.wav", Baked = true },
        // PHD Flopper (the original-the original perk, in the original only as Der Riese's machine model): its whole body is one material, so the sign light finds the top
        new Perk { Id = "phd", Model = "p7_zm_vending_nuke", Glow = new Color(0.55f, 0.35f, 1f), Jingle = "phd_jingle.wav", Sting = "phd_sting.wav", SignSuffix = "_nuke", Baked = true },
    };

    public static string PrefabPath(Perk perk) => $"{Root}/perk_{perk.Id}.prefab";

    [MenuItem("Perks/1. Import perk machines")]
    public static void ImportAll()
    {
        foreach (string dir in new[] { GeneratedDir, TextureDir, MaterialDir })
            Directory.CreateDirectory(dir);
        foreach (Perk perk in Perks)
            Import(perk);
    }

    private static void Import(Perk perk)
    {
        string modelDir = perk.Baked ? Path.GetFullPath(Path.Combine(Application.dataPath, "Perks/Baked", perk.Model)) : Path.Combine(ExportRoot, perk.Model);
        SEModel model = SEModel.Load(Path.Combine(modelDir, perk.Model + "_LOD0.semodel"));
        Debug.Log($"[Perks] {perk.Id}: {model.Bones.Count} bones, {model.Meshes.Count} meshes, {model.Materials.Count} materials");

        var root = new GameObject("perk_" + perk.Id);
        try
        {
            // Every vertex sits on one bone at the origin: a plain mesh, no skinning
            Material[] materials = model.Materials.Select(m => BuildMaterial(m, modelDir, perk)).ToArray();
            Mesh mesh = BuildMesh(model, "perk_" + perk.Id + "_mesh");
            AssetDatabase.CreateAsset(mesh, $"{GeneratedDir}/{mesh.name}.asset");

            var meshGo = new GameObject(mesh.name);
            meshGo.transform.SetParent(root.transform, false);
            meshGo.AddComponent<MeshFilter>().sharedMesh = mesh;
            meshGo.AddComponent<MeshRenderer>().sharedMaterials = model.Meshes
                .Select(m => m.MaterialIndices.Length > 0 && m.MaterialIndices[0] >= 0 ? materials[m.MaterialIndices[0]] : materials[0])
                .ToArray();

            var colliders = new GameObject("Colliders").transform;
            colliders.SetParent(root.transform, false);
            var box = colliders.gameObject.AddComponent<BoxCollider>();
            box.center = mesh.bounds.center;
            box.size = mesh.bounds.size;

            // The round sign's light in front of it, and a softer one washing over the front panel ("Drink Jugger-Nog")
            Vector3? sign = Centre(model, perk.SignSuffix);
            AddLight(root.transform, "SignLight", sign, new Vector3(0f, 0f, 0.8f), perk.Glow, 1.8f * perk.Bright, 5f);
            AddLight(root.transform, "PanelLight", new Vector3(0f, mesh.bounds.size.y * 0.68f, mesh.bounds.max.z), new Vector3(0f, 0f, 1.2f),
                Color.Lerp(perk.Glow, Color.white, 0.6f), 0.5f * perk.Bright, 3.5f);

            AddAudio(root.transform, "Audio_Jingle", perk.Jingle, false, 0.3f);
            AddAudio(root.transform, "Audio_Sting", perk.Sting, false, 0.5f);
            AddAudio(root.transform, "Audio_Hum", "perk_hum.wav", true, 0.15f);

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(perk));
            AssetDatabase.SaveAssets();
            Debug.Log($"[Perks] Prefab saved: {PrefabPath(perk)} (bounds {mesh.bounds.size} m, centre {mesh.bounds.center})");
        }
        finally
        {
            Object.DestroyImmediate(root);
        }
    }

    // ---------- Geometry ----------

    private static Mesh BuildMesh(SEModel model, string name)
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var submeshes = new List<int[]>();

        foreach (SEMesh m in model.Meshes)
        {
            int baseIndex = vertices.Count;
            for (int v = 0; v < m.VertexCount; v++)
            {
                vertices.Add(ToUnity(m.Positions[v]));
                normals.Add(m.Normals != null ? ToUnityDir(m.Normals[v]).normalized : Vector3.up);
                uvs.Add(m.UVs != null ? new Vector2(m.UVs[v * 2], 1f - m.UVs[v * 2 + 1]) : Vector2.zero);
            }

            int[] tris = m.Faces.Select(i => i + baseIndex).ToArray();

            // Keep the winding whose face normals agree with the vertex normals
            int agree = 0;
            for (int f = 0; f < tris.Length; f += 3)
            {
                Vector3 a = vertices[tris[f]], b = vertices[tris[f + 1]], c = vertices[tris[f + 2]];
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), normals[tris[f]]) > 0f)
                    agree++;
            }
            if (agree * 2 < tris.Length / 3)
            {
                for (int f = 0; f < tris.Length; f += 3)
                    (tris[f + 1], tris[f + 2]) = (tris[f + 2], tris[f + 1]);
            }
            submeshes.Add(tris);
        }

        var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = submeshes.Count;
        for (int i = 0; i < submeshes.Count; i++)
            mesh.SetTriangles(submeshes[i], i);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    /// <summary>Middle of the top of the meshes whose material name ends with <paramref name="suffix"/>, or null.</summary>
    private static Vector3? Centre(SEModel model, string suffix)
    {
        // Only vertices the faces use: exports can carry unused ones elsewhere on the model
        var points = model.Meshes
            .Where(m => m.MaterialIndices.Length > 0 && m.MaterialIndices[0] >= 0 && model.Materials[m.MaterialIndices[0]].Name.EndsWith(suffix))
            .SelectMany(m => m.Faces.Distinct().Select(i => ToUnity(m.Positions[i])))
            .ToArray();
        if (points.Length == 0)
            return null;
        // The sign material also covers decals down the front: only its top metre, the round sign on the pole
        float top = points.Max(p => p.y);
        points = points.Where(p => p.y > top - 1f).ToArray();
        var bounds = new Bounds(points[0], Vector3.zero);
        foreach (Vector3 p in points)
            bounds.Encapsulate(p);
        Debug.Log($"[Perks] {suffix} meshes, top metre: bounds {bounds.center} size {bounds.size}");
        return bounds.center;
    }

    private static void AddLight(Transform root, string name, Vector3? at, Vector3 offset, Color color, float intensity, float range)
    {
        if (at == null)
        {
            Debug.LogWarning($"[Perks] No mesh for {name}; light left out");
            return;
        }
        var go = new GameObject(name);
        go.transform.SetParent(root, false);
        go.transform.localPosition = at.Value + offset;
        var light = go.AddComponent<Light>();
        light.type = LightType.Point;
        light.color = color;
        light.intensity = intensity;
        light.range = range; // the plugin multiplies by the machine's scale
        light.shadows = LightShadows.None;
    }

    // ---------- Materials ----------

    private static Material BuildMaterial(SEMaterial se, string modelDir, Perk perk)
    {
        string path = $"{MaterialDir}/{se.Name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Standard (Specular setup)")) { name = se.Name };
            AssetDatabase.CreateAsset(mat, path);
        }

        string imageDir = Path.Combine(modelDir, "_images", se.Name);
        string diffuse = SourceImage(modelDir, se.DiffuseMap);
        string normal = SourceImage(modelDir, se.NormalMap);
        string spec = SourceImage(modelDir, se.SpecularMap);
        string gloss = FindBySuffix(imageDir, "_g.png");
        string occlusion = FindBySuffix(imageDir, "_o.png");

        Texture2D albedo = null;
        if (diffuse != null)
        {
            albedo = Vibrant(ImportTexture(diffuse, TextureImporterType.Default, true));
            mat.SetTexture("_MainTex", albedo);
            mat.color = Color.white;
        }
        else
        {
            mat.SetTexture("_MainTex", null);
            mat.color = Color.gray;
        }

        if (normal != null)
        {
            mat.SetTexture("_BumpMap", ImportTexture(normal, TextureImporterType.NormalMap, false));
            mat.SetFloat("_BumpScale", 1f);
            mat.EnableKeyword("_NORMALMAP");
        }
        else
        {
            mat.SetTexture("_BumpMap", null);
            mat.DisableKeyword("_NORMALMAP");
        }

        if (spec != null)
        {
            mat.SetTexture("_SpecGlossMap", BuildSpecGloss(spec, gloss));
            mat.SetFloat("_GlossMapScale", 1f);
            mat.EnableKeyword("_SPECGLOSSMAP");
        }
        else
        {
            mat.SetTexture("_SpecGlossMap", null);
            mat.DisableKeyword("_SPECGLOSSMAP");
            mat.SetColor("_SpecColor", new Color(0.2f, 0.2f, 0.2f));
            mat.SetFloat("_Glossiness", 0.35f);
        }

        if (occlusion != null)
            mat.SetTexture("_OcclusionMap", ImportTexture(occlusion, TextureImporterType.Default, false));

        mat.DisableKeyword("_EMISSION");
        mat.SetColor("_EmissionColor", Color.black);
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.EmissiveIsBlack;

        // The sign lights up with its own picture; the plugin switches emission off when the machine has no power
        if (se.Name.EndsWith(perk.SignSuffix) && albedo != null)
            SetEmission(mat, albedo, new Color(0.9f, 0.82f, 0.82f) * perk.Bright);

        // The fridge coil on the back: a cut-out card
        if (se.Name.EndsWith("_coil") && albedo != null)
            SetCutout(mat);

        // Glass (the window over Stamin-Up's five bottles, the Speed Cola and Mule Kick fronts): see-through and
        // glossy, a faint dirty tint, so what's behind it shows (drawn solid it was a grey panel)
        if (se.Name.EndsWith("_glass"))
            SetGlass(mat);

        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void SetGlass(Material mat)
    {
        mat.SetTexture("_SpecGlossMap", null);
        mat.DisableKeyword("_SPECGLOSSMAP");
        mat.SetColor("_SpecColor", new Color(0.35f, 0.35f, 0.35f));
        mat.SetFloat("_Glossiness", 0.92f);
        // Standard's "Fade" (the game renders it; "Transparent" came out black on the perk bottle)
        mat.SetFloat("_Mode", 2f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.EnableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        Color c = mat.color;
        c.a = 0.18f;
        mat.color = c;
    }

    private static void SetEmission(Material mat, Texture2D map, Color color)
    {
        mat.SetTexture("_EmissionMap", map);
        mat.SetColor("_EmissionColor", color);
        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        mat.EnableKeyword("_EMISSION");
    }

    private static void SetCutout(Material mat)
    {
        mat.SetFloat("_Mode", 1f);
        mat.SetFloat("_Cutoff", 0.5f);
        mat.SetOverrideTag("RenderType", "TransparentCutout");
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
        mat.SetInt("_ZWrite", 1);
        mat.EnableKeyword("_ALPHATEST_ON");
        mat.DisableKeyword("_ALPHABLEND_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
    }

    private static string SourceImage(string modelDir, string relative)
    {
        if (string.IsNullOrEmpty(relative))
            return null;
        string rel = relative.Replace(@"\\", @"\");
        if (Path.GetFileName(rel).StartsWith("$"))
            return null;
        string full = Path.Combine(modelDir, rel);
        return File.Exists(full) ? full : null;
    }

    private static string FindBySuffix(string dir, string suffix) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + suffix).FirstOrDefault(f => !Path.GetFileName(f).StartsWith("$")) : null;

    // The machines' colour maps at twice their size (tools\upscale_textures.py), preferred over the originals
    private static string UpscaledDir => Path.GetFullPath(Path.Combine(Application.dataPath, "../../upscaled"));

    private static Texture2D ImportTexture(string source, TextureImporterType type, bool srgb)
    {
        if (type == TextureImporterType.Default && srgb)
        {
            string bigger = Path.Combine(UpscaledDir, Path.GetFileName(source));
            if (File.Exists(bigger))
                source = bigger;
        }
        string assetPath = $"{TextureDir}/{Path.GetFileName(source)}";
        if (!File.Exists(assetPath) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(assetPath))
        {
            File.Copy(source, assetPath, true);
            AssetDatabase.ImportAsset(assetPath);
        }

        var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
        if (importer.textureType != type || importer.sRGBTexture != srgb)
        {
            importer.textureType = type;
            importer.sRGBTexture = srgb;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    // The original's colour maps look washed out under Unity's Standard shader (and its grey spec maps take colour off the
    // diffuse): more saturation and a little contrast on the colour map, a weaker spec map
    private const float Saturation = 1.35f, Contrast = 1.08f, SpecStrength = 0.65f;

    // Derived maps are rebuilt when this script is newer than them (the numbers above changed)
    private static string VersionSource => Path.Combine(Application.dataPath, "Perks/Editor/PerkImporter.cs");

    private static bool Stale(string derived, string source) =>
        !File.Exists(derived) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(derived)
        || File.GetLastWriteTimeUtc(VersionSource) > File.GetLastWriteTimeUtc(derived);

    /// <summary>A copy of the colour map (name_v.png) with <see cref="Saturation"/> and <see cref="Contrast"/> applied; alpha kept.</summary>
    private static Texture2D Vibrant(Texture2D source)
    {
        string sourcePath = AssetDatabase.GetAssetPath(source);
        string assetPath = $"{TextureDir}/{Path.GetFileNameWithoutExtension(sourcePath)}_v.png";
        if (Stale(assetPath, sourcePath))
        {
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            tex.LoadImage(File.ReadAllBytes(sourcePath));
            Color[] pixels = tex.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color c = pixels[i];
                Color.RGBToHSV(c, out float h, out float s, out float v);
                Color o = Color.HSVToRGB(h, Mathf.Clamp01(s * Saturation), v);
                o.r = Mathf.Clamp01((o.r - 0.5f) * Contrast + 0.5f);
                o.g = Mathf.Clamp01((o.g - 0.5f) * Contrast + 0.5f);
                o.b = Mathf.Clamp01((o.b - 0.5f) * Contrast + 0.5f);
                o.a = c.a;
                pixels[i] = o;
            }
            tex.SetPixels(pixels);
            File.WriteAllBytes(assetPath, tex.EncodeToPNG());
            Object.DestroyImmediate(tex);
            AssetDatabase.ImportAsset(assetPath);
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    /// <summary>Standard (Specular setup) reads smoothness from the specular map's alpha, so pack _g into _s.a.</summary>
    private static Texture2D BuildSpecGloss(string specSource, string glossSource)
    {
        string assetPath = $"{TextureDir}/{Path.GetFileNameWithoutExtension(specSource)}_sg.png";
        if (Stale(assetPath, specSource))
        {
            var spec = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            spec.LoadImage(File.ReadAllBytes(specSource));
            Texture2D gloss = null;
            if (glossSource != null)
            {
                gloss = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                gloss.LoadImage(File.ReadAllBytes(glossSource));
            }

            int w = spec.width, h = spec.height;
            Color[] pixels = spec.GetPixels();
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    pixels[i].r *= SpecStrength;
                    pixels[i].g *= SpecStrength;
                    pixels[i].b *= SpecStrength;
                    pixels[i].a = gloss != null ? gloss.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h).r : 0.35f;
                }
            }
            spec.SetPixels(pixels);
            File.WriteAllBytes(assetPath, spec.EncodeToPNG());
            Object.DestroyImmediate(spec);
            if (gloss != null)
                Object.DestroyImmediate(gloss);
            AssetDatabase.ImportAsset(assetPath);
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
    }

    // ---------- Shared with the bottle import ----------

    /// <summary>A colour map imported with the machines' approved vibrancy.</summary>
    public static Texture2D VibrantCopy(string source) => Vibrant(ImportTexture(source, TextureImporterType.Default, true));

    public static Texture2D Import(string source, TextureImporterType type, bool srgb) => ImportTexture(source, type, srgb);

    /// <summary>A spec map with its gloss in alpha, at the machines' approved strength.</summary>
    public static Texture2D SpecGloss(string spec, string gloss) => BuildSpecGloss(spec, gloss);

    // ---------- Audio ----------

    private static void AddAudio(Transform parent, string name, string file, bool loop, float volume)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>($"{AudioDir}/{file}");
        if (clip == null)
        {
            Debug.LogError($"[Perks] Missing audio clip {file} for {name}");
            return;
        }

        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, 2.5f, 0.5f);

        var source = go.AddComponent<AudioSource>();
        source.clip = clip;
        source.loop = loop;
        source.playOnAwake = false;
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 1f;
        source.maxDistance = 15f;
        source.dopplerLevel = 0f;
        source.volume = volume;
    }
}
