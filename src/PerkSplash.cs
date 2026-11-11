using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace PerkBeverages
{
    /// <summary>
    /// The rest of the drink splashed where the bottle smashes: the original's throw always goes off to the right, so a ray
    /// follows it (right, a little forward and down) to the first surface. On the floor that's a splash; on a wall or
    /// a crate a splash lying flat on it, plus a smaller puddle on the floor below where it ran down. In the perk's
    /// own drink colour, with the bottle's glass shards scattered on the floor where it smashed. Each one fades after a
    /// while and only a few are kept, for performance.
    /// </summary>
    internal class PerkSplash : MonoBehaviour
    {
        private const float Life = 25f, FadeTime = 4f;
        private const int MaxSplashes = 20; // splash, puddle and shards each count: about 8 smashes
        private const float ThrowReach = 2.5f, DropReach = 2.5f;
        private const float SplashSize = 0.45f, PuddleSize = 0.3f, ShardsSize = 0.55f;
        private static readonly Color Glass = new Color(0.85f, 0.88f, 0.9f, 0.7f);

        private static readonly List<PerkSplash> Live = new List<PerkSplash>();
        private static Texture2D[] _textures, _shardTextures;
        private static Shader _shader;

        private Material _material;
        private Color _colour;
        private float _born;

        /// <summary>The bottle smashing for <paramref name="perk"/>, thrown by <paramref name="player"/>.</summary>
        public static void Smash(PerkDef perk, Player player)
        {
            try
            {
                Camera camera = PerkInteraction.ActiveCamera;
                if (perk == null || camera == null)
                    return;
                Transform view = camera.transform;
                Transform body = player != null ? ((Component)player).transform : null;
                Vector3 flatForward = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
                Vector3 right = Vector3.Cross(Vector3.up, flatForward);
                Vector3 throwDir = (right + flatForward * 0.6f + Vector3.down * 0.5f).normalized;

                if (Cast(view.position, throwDir, ThrowReach, body, out RaycastHit hit))
                {
                    Spawn(perk, hit.point, hit.normal, SplashSize);
                    if (Mathf.Abs(hit.normal.y) >= 0.6f)
                        Shards(hit.point, hit.normal);
                    // Hit something upright: the rest runs down to the floor below, with the glass
                    else if (Cast(hit.point + hit.normal * 0.06f, Vector3.down, DropReach, body, out RaycastHit floor))
                    {
                        Spawn(perk, floor.point, floor.normal, PuddleSize);
                        Shards(floor.point, floor.normal);
                    }
                }
                else if (Cast(view.position + throwDir * ThrowReach, Vector3.down, DropReach, body, out RaycastHit below))
                {
                    // Nothing in the way: it lands on the floor at the end of the throw
                    Spawn(perk, below.point, below.normal, SplashSize);
                    Shards(below.point, below.normal);
                }
            }
            catch (System.Exception e)
            {
                PerkPlugin.Log.LogWarning($"Perk splash skipped: {e.Message}");
            }
        }

        /// <summary>The nearest solid hit along the ray that isn't the player.</summary>
        private static bool Cast(Vector3 from, Vector3 dir, float reach, Transform player, out RaycastHit nearest)
        {
            nearest = default;
            bool found = false;
            foreach (RaycastHit h in Physics.RaycastAll(from, dir, reach, ~0, QueryTriggerInteraction.Ignore))
            {
                if (player != null && h.collider.transform.IsChildOf(player))
                    continue;
                if (!found || h.distance < nearest.distance)
                {
                    nearest = h;
                    found = true;
                }
            }
            return found;
        }

        private static void Spawn(PerkDef perk, Vector3 point, Vector3 normal, float size) =>
            Decal($"perk_splash_{perk.Id}", point, normal, 0.004f, size, Muted(perk.Liquid), Textures());

        // Spilt drink reads darker and duller than in the bottle (the user, 2026-10-31: less vibrant)
        private const float SplashSaturation = 0.65f, SplashBrightness = 0.55f;

        private static Color Muted(Color c)
        {
            Color.RGBToHSV(c, out float h, out float s, out float v);
            Color muted = Color.HSVToRGB(h, s * SplashSaturation, v * SplashBrightness);
            muted.a = c.a;
            return muted;
        }

        /// <summary>The bottle's glass on the floor, a little above the splash so it lies on top.</summary>
        private static void Shards(Vector3 point, Vector3 normal) =>
            Decal("perk_shards", point, normal, 0.007f, ShardsSize, Glass, ShardTextures());

        private static void Decal(string name, Vector3 point, Vector3 normal, float lift, float size, Color colour, Texture2D[] textures)
        {
            if (Shader() == null)
                return;
            while (Live.Count >= MaxSplashes)
            {
                PerkSplash oldest = Live[0];
                Live.RemoveAt(0);
                if (oldest != null)
                    Destroy(oldest.gameObject);
            }
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            Destroy(quad.GetComponent<Collider>());
            // Facing out of the surface (a quad's visible side looks down its -Z), a hair off it, turned at random
            quad.transform.position = point + normal * lift;
            quad.transform.rotation = Quaternion.LookRotation(-normal) * Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));
            quad.transform.localScale = Vector3.one * size * Random.Range(0.85f, 1.15f);

            var splash = quad.AddComponent<PerkSplash>();
            splash._colour = colour;
            splash._material = new Material(_shader) { mainTexture = textures[Random.Range(0, textures.Length)], color = colour };
            var renderer = quad.GetComponent<MeshRenderer>();
            renderer.sharedMaterial = splash._material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            splash._born = Time.time;
            Live.Add(splash);
        }

        private void Update()
        {
            float age = Time.time - _born;
            if (age >= Life)
            {
                Live.Remove(this);
                Destroy(gameObject);
                return;
            }
            float fade = Mathf.Clamp01((Life - age) / FadeTime);
            Color c = _colour;
            c.a *= fade;
            _material.color = c;
        }

        private void OnDestroy()
        {
            Live.Remove(this);
            if (_material != null)
                Destroy(_material);
        }

        /// <summary>Sprites/Default: always in the game (the Ray Gun's chalk uses it), and it blends by alpha.</summary>
        private static Shader Shader() => _shader != null ? _shader : _shader = UnityEngine.Shader.Find("Sprites/Default");

        // ---------- The splash pictures ----------

        /// <summary>A few splash shapes, made once: a wet middle, lobes round it and droplets flung out.</summary>
        private static Texture2D[] Textures()
        {
            if (_textures != null)
                return _textures;
            _textures = new Texture2D[3];
            for (int i = 0; i < _textures.Length; i++)
                _textures[i] = MakeSplash(1234 + i * 77);
            return _textures;
        }

        /// <summary>A few scatters of glass shards, made once.</summary>
        private static Texture2D[] ShardTextures()
        {
            if (_shardTextures != null)
                return _shardTextures;
            _shardTextures = new Texture2D[3];
            for (int i = 0; i < _shardTextures.Length; i++)
                _shardTextures[i] = MakeShards(4321 + i * 91);
            return _shardTextures;
        }

        /// <summary>
        /// Broken glass seen from above: small sharp polygons (3 to 5 corners), denser in the middle, pale and
        /// see-through with brighter edges and a few glints, like the shards on EFT's maps.
        /// </summary>
        private static Texture2D MakeShards(int seed)
        {
            const int size = 256;
            var rng = new System.Random(seed);
            float R() => (float)rng.NextDouble();
            var pixels = new Color[size * size];
            int count = 35 + rng.Next(20);
            for (int n = 0; n < count; n++)
            {
                float a = R() * Mathf.PI * 2f, d = Mathf.Pow(R(), 1.6f) * 0.42f;
                var c = new Vector2(0.5f + Mathf.Cos(a) * d, 0.5f + Mathf.Sin(a) * d);
                float r = (0.012f + R() * 0.035f) * (1f - d);
                int corners = 3 + rng.Next(3);
                var poly = new Vector2[corners];
                float spin = R() * Mathf.PI * 2f;
                for (int k = 0; k < corners; k++)
                {
                    float t = spin + (k + R() * 0.6f) / corners * Mathf.PI * 2f;
                    poly[k] = c + new Vector2(Mathf.Cos(t), Mathf.Sin(t)) * r * (0.5f + R());
                }
                float body = 0.35f + R() * 0.25f;
                int x0 = Mathf.Max(0, (int)((c.x - r * 1.6f) * size)), x1 = Mathf.Min(size - 1, (int)((c.x + r * 1.6f) * size));
                int y0 = Mathf.Max(0, (int)((c.y - r * 1.6f) * size)), y1 = Mathf.Min(size - 1, (int)((c.y + r * 1.6f) * size));
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        var p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
                        if (!Inside(poly, p))
                            continue;
                        // A brighter rim along the shard's edges, the odd glint inside
                        bool rim = EdgeDistance(poly, p) * size < 1.5f;
                        float alpha = rim ? 0.95f : body;
                        float shade = rim ? 1f : 0.7f + R() * 0.15f;
                        if (R() < 0.01f)
                            shade = alpha = 1f;
                        int i = y * size + x;
                        if (alpha > pixels[i].a)
                            pixels[i] = new Color(shade, shade, shade, alpha);
                    }
                }
            }
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private static bool Inside(Vector2[] poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].y > p.y) != (poly[j].y > p.y)
                    && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        private static float EdgeDistance(Vector2[] poly, Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                Vector2 a = poly[j], b = poly[i], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-9f));
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }

        private static Texture2D MakeSplash(int seed)
        {
            const int size = 128;
            var rng = new System.Random(seed);
            float R() => (float)rng.NextDouble();
            // Blobs: (centre x, centre y, radius) in 0..1 texture space
            var blobs = new List<Vector3> { new Vector3(0.5f, 0.5f, 0.2f) };
            int lobes = 6 + rng.Next(4);
            for (int i = 0; i < lobes; i++)
            {
                float a = R() * Mathf.PI * 2f, d = 0.1f + R() * 0.14f;
                blobs.Add(new Vector3(0.5f + Mathf.Cos(a) * d, 0.5f + Mathf.Sin(a) * d, 0.06f + R() * 0.08f));
            }
            int drops = 10 + rng.Next(8);
            for (int i = 0; i < drops; i++)
            {
                float a = R() * Mathf.PI * 2f, d = 0.26f + R() * 0.2f;
                blobs.Add(new Vector3(0.5f + Mathf.Cos(a) * d, 0.5f + Mathf.Sin(a) * d, 0.012f + R() * 0.025f));
            }

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var p = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
                    // A soft union of the blobs (metaball-like), cut to a slightly soft edge
                    float field = 0f;
                    foreach (Vector3 b in blobs)
                    {
                        float d = Vector2.Distance(p, new Vector2(b.x, b.y)) / b.z;
                        field += Mathf.Max(0f, 1f - d * d);
                    }
                    float alpha = Mathf.Clamp01((field - 0.25f) * 6f) * 0.85f;
                    // Darker, thicker middle; thinner at the edges
                    float shade = Mathf.Lerp(1f, 0.75f, Mathf.Clamp01(field - 0.6f));
                    pixels[y * size + x] = new Color(shade, shade, shade, alpha);
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
