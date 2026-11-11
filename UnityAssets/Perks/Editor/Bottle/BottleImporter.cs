using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using PerkBottle.ModelFormats;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PerkBottle.Editor
{
    /// <summary>
    /// Builds each perk's bottle as an EFT drink, from the original's own bottle models and viewmodel animation (Greyhound
    /// exports), the way the Ray Gun is built:
    ///   item_perk_bottle_&lt;id&gt;_anim       EFT's hands rig (from the beer bottle) with the original's view bottle and the the original
    ///                                     drink retargeted onto EFT's arms, driven by the rebuilt beer controller
    ///   item_perk_bottle_&lt;id&gt;_container  UsableHandsPrefab and BaseSoundPlayer (what the game reads to hold and drink it)
    ///   item_perk_bottle_&lt;id&gt;_loot       the original's world bottle, for the ground, the inventory picture and display shelves
    ///
    /// The original's drink (vm_perk_bottle_t7_drink, 65 frames) is split over EFT's drink states: bottle up and opened, the gulp
    /// (EFT repeats it while the drink lasts), and the bottle thrown away. Its notetracks become the sound events. The
    /// burp and the blur come after, once the gun is back out (client side).
    /// </summary>
    public static class BottleImporter
    {
        public class Bottle
        {
            public string Id;     // item_perk_bottle_<Id>_*
            public string View;   // The original view model (first person)
            public string World;  // The original world model (loot, icon)
            // Jugg's signed-off colours; the others work theirs out from the original's own maps (null)
            public Color? Glass, Liquid, Cap;
        }

        public static readonly Bottle[] Bottles =
        {
            new Bottle { Id = "jugg", View = "wpn_t7_zmb_perk_bottle_jugg_view", World = "wpn_t7_zmb_perk_bottle_jugg_world",
                Glass = new Color(0.55f, 0.12f, 0.1f), Liquid = new Color(0.62f, 0.22f, 0.2f), Cap = new Color(2.2f, 1.9f, 1.9f) },
            new Bottle { Id = "speed", View = "wpn_t7_zmb_perk_bottle_speed_view", World = "wpn_t7_zmb_perk_bottle_speed_world" },
            new Bottle { Id = "dtap", View = "wpn_t7_zmb_perk_bottle_2tap_view", World = "wpn_t7_zmb_perk_bottle_2tap_world" },
            new Bottle { Id = "deadshot", View = "wpn_t7_zmb_perk_bottle_ds_view", World = "wpn_t7_zmb_perk_bottle_ds_world" },
            new Bottle { Id = "stamin", View = "wpn_t7_zmb_perk_bottle_stimup_view", World = "wpn_t7_zmb_perk_bottle_stimup_world" },
            new Bottle { Id = "revive", View = "wpn_t7_zmb_perk_bottle_rev_view", World = "wpn_t7_zmb_perk_bottle_rev_world" },
            new Bottle { Id = "mule", View = "wpn_t7_zmb_perk_bottle_mule_view", World = "wpn_t7_zmb_perk_bottle_mule_world" },
            new Bottle { Id = "widow", View = "wpn_t7_zmb_perk_bottle_wido_view", World = "wpn_t7_zmb_perk_bottle_wido_world" },
            // PHD Flopper has no the original bottle: Widow's Wine's, in PHD's lavender
            new Bottle { Id = "phd", View = "wpn_t7_zmb_perk_bottle_wido_view", World = "wpn_t7_zmb_perk_bottle_wido_world",
                Glass = new Color(0.42f, 0.3f, 0.7f), Liquid = new Color(0.55f, 0.42f, 0.85f) },
        };

        public static string RigName(Bottle b) => $"item_perk_bottle_{b.Id}_anim";
        public static string ContainerName(Bottle b) => $"item_perk_bottle_{b.Id}_container";
        public static string LootName(Bottle b) => $"item_perk_bottle_{b.Id}_loot";
        public static string PrefabPath(string name) => $"{RgPaths.Root}/{name}.prefab";

        private const string Drink = "vm_perk_bottle_t7_drink";
        private const string Idle = "vm_perk_bottle_t7_idle";

        // The original's drink over EFT's beer states (frames of vm_perk_bottle_t7_drink): up and opened, the gulp, thrown away.
        // "BREAK -1" plays the opening backwards (EFT: interrupted before drinking)
        // Up and opened, to the mouth with the one swig (the original's swallow at 29); then held still at the mouth (EFT repeats
        // the gulp state for as long as the drink is active: a still pose repeats unseen); then thrown away from it
        private const int AtMouth = 32, HoldEnd = AtMouth;
        private static readonly Dictionary<string, (string anim, int first, int last)> HandsMap = new Dictionary<string, (string, int, int)>
        {
            ["OUT TO USE"] = (Drink, 0, AtMouth),
            ["USE"] = (Drink, AtMouth, HoldEnd),
            ["USE_1"] = (Drink, AtMouth, HoldEnd),
            ["USE TO OUT"] = (Drink, AtMouth, 64),
            ["BREAK -1"] = (Drink, 0, AtMouth),
        };

        /// <summary>
        /// Gulp states: a still hold at the mouth, silent, long enough that it doesn't repeat before the drink ends. A
        /// one-frame hold restarted many times a second, each restart firing EFT's IdleStart, and the drink never
        /// finished (the bottle wasn't used up).
        /// </summary>
        private static readonly HashSet<string> Gulps = new HashSet<string> { "USE", "USE_1" };

        /// <summary>
        /// The original's bottle is bigger than EFT's (31 cm against the beer's 23): shrunk about the grip, so the hand closes
        /// round it instead of the fingers sinking in.
        /// </summary>
        private const float ViewBottleScale = 0.92f;

        /// <summary>The bottle turned about its own length so its label faces the player while drinking (degrees).</summary>
        private const float LabelSpin = 180f;

        /// <summary>the original's notetracks to EFT sound events ("Snd" + name in the BaseSoundPlayer), and the clip for each.</summary>
        private static readonly Dictionary<string, (string sound, string clip)> Notes = new Dictionary<string, (string, string)>
        {
            ["evt_perk_bottle_open"] = ("Open", "bottle_open"),
            ["evt_perk_swallow"] = ("Drink", "bottle_swallow"),
            ["evt_bottle_break"] = ("Break", "bottle_break"),
        };

        /// <summary>The cap pops off with the open sound: hidden from then on.</summary>
        private const string CapBone = "tag_top_animate";
        private const int CapOffAt = 16;

        private const float CmToM = 0.01f;

        /// <summary>the original's drink is quick for EFT's pace: its states play at this speed (sounds stay in step).</summary>
        private const float DrinkSpeed = 0.8f;

        [MenuItem("Perks/3. Import perk bottles")]
        public static void ImportAll()
        {
            AssetDatabase.DeleteAsset(RgPaths.Clips);
            RgPaths.EnsureDirs();
            foreach (Bottle b in Bottles)
                Import(b);
            AssetDatabase.SaveAssets();
        }

        private static void Import(Bottle b)
        {
            RgClassic.ModelName = b.View;
            SrcModel gun = RgClassic.LoadModel();
            SrcModel bind = SrcModel.Load(Path.Combine(RgPaths.ModelDir, "am_t5_thundergun.xmodel_bin"));
            Debug.Log($"[Perks] {b.Id} bottle: {gun.Bones.Count} bones, {gun.Vertices.Count} verts, {gun.Faces.Count} faces");
            var warnings = new List<string>();

            var rig = new Dictionary<string, Transform>();
            GameObject root = RgRig.Build(RigName(b), rig);
            try
            {
                Transform weapon = rig["weapon"];
                var jgun = new GameObject(gun.Bones[0].Name).transform;
                jgun.SetParent(weapon, false);
                jgun.gameObject.layer = weapon.gameObject.layer;
                Quaternion jgunLocal = Quaternion.Inverse(weapon.rotation) * RgSpace.Rot(gun.Bones[0].Rest);
                jgun.localRotation = jgunLocal;
                jgun.localScale = Vector3.one * ViewBottleScale;
                Dictionary<string, Transform> gunBones = BuildGunSkeleton(gun, jgun);

                var materials = new Dictionary<int, Material>();
                for (int m = 0; m < gun.Materials.Count; m++)
                    materials[m] = BuildMaterial(gun.Materials[m].Name, RgClassic.ModelDir, b);
                BuildGunMeshes(gun, gunBones, materials, b);

                // The beer's own bottle and cap under the weapon bone aren't part of the rig (RgRig skips meshes), but
                // their empty holders are: nothing to show there
                var notes = new List<(int frame, string note)>();
                SrcAnim drink = RgClassic.LoadAnim(Drink, gun, bind, out notes);
                SrcAnim idle = RgClassic.LoadAnim(Idle, gun, bind, out _);

                // The Ray Gun's anchoring (tested in game: the drink sits up and to the right); the client's F12 "Drink
            // view" sliders move it live while a bottle is in the hands, and the result is folded in here
            RgRetarget.AnchorBlend = new Vector3(0.5f, 0.2f, 1f);
            RgRetarget.ViewNudge = new Vector3(0f, 0.01f, 0.14f);
            // Out of the right hand's fingers a little (they sank into the bottle in game), to the left
            RgRetarget.GunShift = new Vector3(-0.008f, 0f, 0f);
            var retarget = new RgRetarget(root.transform, rig, gun, gunBones, jgunLocal, bind, idle);
                Debug.Log($"[Perks] View space offset: {retarget.Offset}, shoulders moved {retarget.ShoulderShifts}");
                retarget.PlaceMarkers(idle, null);

                string capPath = gunBones.TryGetValue(CapBone, out Transform cap) ? RgRig.PathOf(cap, root.transform) : null;
                var handsClips = new Dictionary<string, AnimationClip>();
                var clips = new Dictionary<string, AnimationClip>();
                foreach (var (state, (anim, first, last)) in HandsMap)
                {
                    bool gulp = Gulps.Contains(state);
                    string key = $"{b.Id}_{anim.Replace("vm_perk_bottle_t7_", "")}_{first}_{last}" + (gulp ? "_loop" : "");
                    if (!clips.TryGetValue(key, out AnimationClip clip))
                    {
                        var clipWarnings = new List<string>();
                        clip = retarget.BakeClip(key, anim == Drink ? drink : idle, first, last, false, clipWarnings);
                        if (capPath != null)
                            CapCurve(clip, capPath, first, last, drink.FrameRate);
                        if (gulp)
                            Hold(clip, HoldSeconds);
                        string clipPath = $"{RgPaths.Clips}/{key}.anim";
                        AssetDatabase.DeleteAsset(clipPath);
                        AssetDatabase.CreateAsset(clip, clipPath);
                        clips[key] = clip;
                        if (clipWarnings.Count > 0)
                            warnings.Add($"{key}: {clipWarnings.Count} frame(s) out of reach, first: {clipWarnings[0]}");
                    }
                    handsClips[state] = clip;
                }
                AnimationClip idleClip = retarget.BakeClip($"{b.Id}_idle", idle, 0, Math.Max(0, idle.Frames.Count - 1), true, warnings);
                AssetDatabase.DeleteAsset($"{RgPaths.Clips}/{idleClip.name}.anim");
                AssetDatabase.CreateAsset(idleClip, $"{RgPaths.Clips}/{idleClip.name}.anim");

                AnimatorController controller = RgController.Build($"{RgPaths.Generated}/{RigName(b)}.controller", root.transform, handsClips, idleClip,
                    // Pieces that meet on the same frame carry straight on; into the throw it blends (the drink can end mid-gulp)
                    StateSounds(notes), new HashSet<(string, string)> { ("OUT TO USE", "USE"), ("USE", "USE_1"), ("USE_1", "USE") },
                    new Dictionary<string, float>
                    {
                        ["OUT TO USE"] = DrinkSpeed, ["USE"] = DrinkSpeed, ["USE_1"] = DrinkSpeed, ["USE TO OUT"] = DrinkSpeed,
                        ["BREAK -1"] = -1.2f,
                    });

                BlendIntoThrow(controller);

                retarget.ApplyFrame(idle, 0);
                var animator = root.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                var links = root.AddComponent<TransformLinks>();
                CopyFields(links, ComponentFields("TransformLinks", RgPaths.BaseModel), r => ResolveInRig(r, root.transform));
                links.Self = root.transform;

                string rigPath = PrefabPath(RigName(b));
                AssetDatabase.DeleteAsset(rigPath);
                GameObject rigPrefab = PrefabUtility.SaveAsPrefabAsset(root, rigPath);
                BuildContainer(b, rigPrefab, controller);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
            BuildLoot(b);
            foreach (string w in warnings)
                Debug.LogWarning("[Perks] " + w);
        }

        /// <summary>The cap's scale in a clip: on until <see cref="CapOffAt"/>, then gone (the clips past it start without it).</summary>
        private static void CapCurve(AnimationClip clip, string path, int first, int last, float fps)
        {
            fps = fps > 0 ? fps : 30f;
            float length = Mathf.Max(1, last - first) / fps;
            var keys = new List<Keyframe>();
            float on = first < CapOffAt ? 1f : 0f;
            keys.Add(new Keyframe(0f, on));
            if (first < CapOffAt && last >= CapOffAt)
            {
                float t = (CapOffAt - first) / fps;
                keys.Add(new Keyframe(t - 0.001f, 1f));
                keys.Add(new Keyframe(t, 0f));
                on = 0f;
            }
            keys.Add(new Keyframe(length, on));
            var curve = new AnimationCurve(keys.ToArray());
            for (int i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Constant);
            }
            foreach (string axis in new[] { "x", "y", "z" })
                clip.SetCurve(path, typeof(Transform), "m_LocalScale." + axis, curve);
        }

        /// <summary>
        /// The drink ends whenever EFT says (mid-hold): the beer's controller cuts straight into the throw, which snapped.
        /// A short blend instead.
        /// </summary>
        private static void BlendIntoThrow(AnimatorController controller)
        {
            AnimatorControllerLayer hands = controller.layers.FirstOrDefault(l => l.name == "Hands");
            if (hands == null)
                return;
            foreach (ChildAnimatorState child in hands.stateMachine.states.Where(s => Gulps.Contains(s.state.name)))
            {
                foreach (AnimatorStateTransition t in child.state.transitions.Where(t => t.destinationState != null && t.destinationState.name == "USE TO OUT"))
                {
                    t.duration = ThrowBlend;
                    t.hasFixedDuration = true;
                    t.offset = 0f;
                }
            }
            EditorUtility.SetDirty(controller);
        }

        private const float ThrowBlend = 0.2f;

        /// <summary>How long the still hold at the mouth lasts (before the state's speed), seconds.</summary>
        private const float HoldSeconds = 1f;

        /// <summary>
        /// The second swig, as normalized time in the hold: as the first ends (the original's swallow is 0.43 s and starts 3 frames
        /// before the hold; at 0.8 speed that leaves ~0.3 s, and the 1 s hold plays in 1.25 s), plus a short beat between the two.
        /// </summary>
        private const float SecondSwigAt = 0.29f;

        /// <summary>The clip's first pose held for <paramref name="seconds"/>: every curve becomes two equal keys.</summary>
        private static void Hold(AnimationClip clip, float seconds)
        {
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                float value = curve.length > 0 ? curve.keys[0].value : 0f;
                var held = new AnimationCurve(new Keyframe(0f, value, 0f, 0f), new Keyframe(seconds, value, 0f, 0f));
                AnimationUtility.SetEditorCurve(clip, binding, held);
            }
        }

        /// <summary>
        /// The clip played forward then backward in one: it ends on the pose it starts with, so the gulp state can repeat
        /// with no jump. Every curve's keys are mirrored about the clip's end.
        /// </summary>
        private static void ThereAndBack(AnimationClip clip)
        {
            float length = clip.length;
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, binding);
                var keys = curve.keys.ToList();
                for (int i = keys.Count - 2; i >= 0; i--)
                {
                    Keyframe k = keys[i];
                    keys.Add(new Keyframe(2f * length - k.time, k.value, -k.outTangent, -k.inTangent));
                }
                var mirrored = new AnimationCurve(keys.ToArray());
                bool constant = binding.propertyName.StartsWith("m_LocalScale");
                for (int i = 0; i < mirrored.length; i++)
                {
                    var mode = constant ? AnimationUtility.TangentMode.Constant : AnimationUtility.TangentMode.Linear;
                    AnimationUtility.SetKeyLeftTangentMode(mirrored, i, mode);
                    AnimationUtility.SetKeyRightTangentMode(mirrored, i, mode);
                }
                AnimationUtility.SetEditorCurve(clip, binding, mirrored);
            }
            clip.EnsureQuaternionContinuity();
        }

        /// <summary>Each state's sound events from the original's notetracks, at normalized time within the state's clip.</summary>
        private static Dictionary<string, List<(float t, string sound)>> StateSounds(List<(int frame, string note)> notes)
        {
            var result = new Dictionary<string, List<(float, string)>>();
            var all = notes.Where(n => n.note.StartsWith("sndnt#")).Select(n => (n.frame, sound: Notes.TryGetValue(n.note.Substring(6), out var s) ? s.sound : null))
                .Where(n => n.sound != null).ToList();
            foreach (var (state, (_, first, last)) in HandsMap)
            {
                var list = all.Where(n => n.frame >= first && n.frame < last || (n.frame == last && last == 64))
                    .Select(n => ((float)(n.frame - first) / Mathf.Max(1, last - first), n.sound)).ToList();
                // Played backwards when interrupted: no sounds. The original's swig comes as the bottle reaches the mouth; a second
                // follows straight after it at the start of the hold (the user, 2026-10-31: the hold can't get shorter, so
                // it's filled with swigging), only on the first pass of the hold
                result[state] = state == "BREAK -1" || state == "USE_1" ? new List<(float, string)>()
                    : state == "USE" ? new List<(float, string)> { (SecondSwigAt, "Drink") }
                    : list;
            }
            return result;
        }

        // ---------- Model ----------

        private static Dictionary<string, Transform> BuildGunSkeleton(SrcModel gun, Transform jgun)
        {
            var bones = new Dictionary<string, Transform> { [gun.Bones[0].Name] = jgun };
            Quaternion[] world = gun.Bones.Select(b => RgSpace.Rot(b.Rest)).ToArray();
            Vector3[] pos = gun.Bones.Select(b => RgSpace.Pos(b.Rest.Pos)).ToArray();
            for (int i = 1; i < gun.Bones.Count; i++)
            {
                SrcBone b = gun.Bones[i];
                var t = new GameObject(b.Name).transform;
                t.SetParent(bones[gun.Bones[b.Parent].Name], false);
                t.gameObject.layer = jgun.gameObject.layer;
                Quaternion parentRot = world[b.Parent];
                t.localPosition = Quaternion.Inverse(parentRot) * (pos[i] - pos[b.Parent]);
                t.localRotation = Quaternion.Inverse(parentRot) * world[i];
                bones[b.Name] = t;
            }
            return bones;
        }

        private static void BuildGunMeshes(SrcModel gun, Dictionary<string, Transform> bones, Dictionary<int, Material> materials, Bottle b)
        {
            foreach (var group in gun.Faces.GroupBy(f => FaceBone(gun, f)))
            {
                string boneName = gun.Bones[group.Key].Name;
                Mesh mesh = BuildMesh(gun, group.ToList(), group.Key, $"{RigName(b)}_{boneName}", out List<int> mats);
                if (mesh == null)
                    continue;
                var go = new GameObject($"{RigName(b)}_{boneName}_LOD0");
                go.layer = bones[boneName].gameObject.layer;
                go.transform.SetParent(bones[boneName], false);
                // Turned about the bottle's length (the original game Z, through every bone of the bottle) so the label faces the player
                Vector3 axis = Quaternion.Inverse(RgSpace.Rot(gun.Bones[group.Key].Rest)) * RgSpace.Dir(new SrcVec { Z = 1f });
                go.transform.localRotation = Quaternion.AngleAxis(LabelSpin, axis);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mats.Select(m => materials[m]).ToArray();
            }
        }

        private static int FaceBone(SrcModel gun, SrcFace f)
        {
            var votes = f.Corners.Select(c => gun.Vertices[c.Vertex].Weights.OrderByDescending(w => w.weight).First().bone).ToList();
            return votes.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        }

        /// <summary>Faces into a mesh in the bone's local space, one submesh per material.</summary>
        private static Mesh BuildMesh(SrcModel gun, List<SrcFace> faces, int bone, string name, out List<int> materialOrder)
        {
            Quaternion inv = Quaternion.Inverse(RgSpace.Rot(gun.Bones[bone].Rest));
            Vector3 origin = RgSpace.Pos(gun.Bones[bone].Rest.Pos);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var unique = new Dictionary<(int, Vector3, Vector2), int>();
            var submeshes = new Dictionary<int, List<int>>();

            foreach (SrcFace f in faces)
            {
                if (!submeshes.TryGetValue(f.Material, out List<int> tris))
                    submeshes[f.Material] = tris = new List<int>();
                var corner = new int[3];
                for (int c = 0; c < 3; c++)
                {
                    SrcCorner k = f.Corners[c];
                    Vector3 n = (inv * RgSpace.Dir(k.Normal)).normalized;
                    var uv = new Vector2(k.U, 1f - k.V);
                    var key = (k.Vertex, n, uv);
                    if (!unique.TryGetValue(key, out int index))
                    {
                        index = vertices.Count;
                        unique[key] = index;
                        vertices.Add(inv * (RgSpace.Pos(gun.Vertices[k.Vertex].Pos) - origin));
                        normals.Add(n);
                        uvs.Add(uv);
                    }
                    corner[c] = index;
                }
                tris.AddRange(corner);
            }
            if (vertices.Count == 0)
            {
                materialOrder = null;
                return null;
            }
            materialOrder = submeshes.Keys.OrderBy(k => k).ToList();
            Mesh mesh = Finish(name, vertices, normals, uvs, materialOrder.Select(m => submeshes[m].ToArray()).ToList());
            string path = $"{RgPaths.Generated}/{name}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static Mesh Finish(string name, List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int[]> submeshes)
        {
            var mesh = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = submeshes.Count;
            for (int i = 0; i < submeshes.Count; i++)
            {
                int[] tris = submeshes[i];
                // The axis mirror flips winding: keep whichever winding agrees with the vertex normals
                int agree = 0;
                for (int t = 0; t < tris.Length; t += 3)
                {
                    Vector3 a = vertices[tris[t]], b = vertices[tris[t + 1]], c = vertices[tris[t + 2]];
                    if (Vector3.Dot(Vector3.Cross(b - a, c - a), normals[tris[t]]) > 0f)
                        agree++;
                }
                if (agree * 2 < tris.Length / 3)
                {
                    for (int t = 0; t < tris.Length; t += 3)
                        (tris[t + 1], tris[t + 2]) = (tris[t + 2], tris[t + 1]);
                }
                mesh.SetTriangles(tris, i);
            }
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();
            return mesh;
        }

        // ---------- Loot model ----------

        /// <summary>
        /// The original's world bottle standing on its base at the origin, like the beer's loot model: LODGroup, a collider and
        /// the beer's PreviewPivot (how it lies in the inventory picture), moved to our bottle's middle.
        /// </summary>
        private static void BuildLoot(Bottle b)
        {
            string dir = Path.Combine(RgClassic.ExportRoot, "xmodels", b.World);
            var se = SEFormats.SEModel.Load(Path.Combine(dir, b.World + "_LOD0.semodel"));
            Material[] materials = se.Materials.Select(m => BuildMaterial(m.Name, dir, b)).ToArray();

            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var submeshes = new List<int[]>();
            var used = new List<Material>();
            foreach (SEFormats.SEMesh m in se.Meshes)
            {
                int baseIndex = vertices.Count;
                for (int v = 0; v < m.VertexCount; v++)
                {
                    // Greyhound's centimetres, the original game Z-up to Unity Y-up (the same mapping as the view model)
                    vertices.Add(RgSpace.Dir(Src(m.Positions[v])) * CmToM);
                    normals.Add(m.Normals != null ? RgSpace.Dir(Src(m.Normals[v])).normalized : Vector3.up);
                    uvs.Add(m.UVs != null ? new Vector2(m.UVs[v * 2], 1f - m.UVs[v * 2 + 1]) : Vector2.zero);
                }
                submeshes.Add(m.Faces.Select(i => i + baseIndex).ToArray());
                used.Add(materials[m.MaterialIndices.Length > 0 && m.MaterialIndices[0] >= 0 ? m.MaterialIndices[0] : 0]);
            }
            string name = LootName(b);
            Mesh mesh = Finish(name + "_mesh", vertices, normals, uvs, submeshes);
            string meshPath = $"{RgPaths.Generated}/{mesh.name}.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);

            var root = new GameObject(name);
            try
            {
                var lod = new GameObject($"{name}_LOD0");
                lod.transform.SetParent(root.transform, false);
                lod.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = lod.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = used.ToArray();

                var collider = new GameObject($"{name}_COLLIDER");
                collider.transform.SetParent(root.transform, false);
                var box = collider.AddComponent<BoxCollider>();
                box.center = mesh.bounds.center;
                box.size = mesh.bounds.size;

                var group = root.AddComponent<LODGroup>();
                group.SetLODs(new[] { new LOD(0.01f, new Renderer[] { renderer }) });

                var pivot = root.AddComponent<PreviewPivot>();
                CopyFields(pivot, ComponentFields("PreviewPivot", "item_drink_beer1", "beer_loot_components.json"), _ => null);
                pivot.pivotPosition = mesh.bounds.center;

                string path = PrefabPath(name);
                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[Perks] Loot prefab {path}: bounds {mesh.bounds.size} m");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        private static SrcVec Src(SEFormats.SEVec3 v) => new SrcVec { X = v.X, Y = v.Y, Z = v.Z };

        // ---------- Container ----------

        private static void BuildContainer(Bottle b, GameObject rigPrefab, AnimatorController controller)
        {
            var root = new GameObject(ContainerName(b));
            try
            {
                Object Resolve(string r)
                {
                    if (r == null) return null;
                    if (r.StartsWith("GameObject:") && r.Contains(".generated")) return rigPrefab;
                    if (r.StartsWith("MB:TransformLinks@")) return rigPrefab.GetComponent<TransformLinks>();
                    if (r.StartsWith("Transform:")) return ResolveInRig(r, rigPrefab.transform);
                    return null;
                }

                var hands = root.AddComponent<UsableHandsPrefab>();
                CopyFields(hands, ComponentFields("UsableHandsPrefab", RgPaths.BaseContainer), Resolve);
                hands._weaponObject = rigPrefab;
                hands._weaponObjectSimple = rigPrefab;
                hands._originalAnimatorController = controller;
                hands._animatorSimple = null;
                hands._animatorSpirit = null;
                hands._fastAnimatorControllerBinaryData = null;
                hands._avatar = null;
                hands.DefaultMuzzlePrefab = null;
                hands.DefaultHeatHazeEffect = null;
                hands.Colliders = new List<Collider>();
                hands.Renderers = new List<Renderer>();
                hands._originallyEnabledComponents = new List<Component>();
                hands.RegisteredComponentsToClean = new List<Component>();
                hands.PoolHistory = new List<string>();
                hands.Hierarchy = null;

                var player = root.AddComponent<BaseSoundPlayer>();
                CopyFields(player, ComponentFields("BaseSoundPlayer", RgPaths.BaseContainer), Resolve);
                AudioClip Clip(string n) => AssetDatabase.LoadAssetAtPath<AudioClip>($"{RgPaths.Audio}/{n}.wav");
                // The original's bottle sounds on EFT's drink events; the burp and the smash are ours
                var foley = new Dictionary<string, string>
                {
                    ["SndDraw"] = "bottle_dispense", ["SndOpen"] = "bottle_open", ["SndDrink"] = "bottle_swallow",
                    ["SndHolster"] = null, ["SndBreak"] = "bottle_break",
                };
                foreach (BaseSoundPlayer_SoundElement e in player.AdditionalSounds)
                {
                    AudioClip c = foley.TryGetValue(e.EventName, out string n) && n != null ? Clip(n) : null;
                    // Events we don't voice keep the beer's references, which our bundle can't carry: silent
                    e.SoundClips = c != null ? new List<AudioClip> { c } : new List<AudioClip>();
                }
                // The burp isn't here: it comes after the gun is back out (the client's PerkDrink)
                foreach (string ev in new[] { "SndBreak" })
                {
                    if (player.AdditionalSounds.All(e => e.EventName != ev))
                        player.AdditionalSounds.Add(new BaseSoundPlayer_SoundElement { EventName = ev, RollOff = 30, Volume = 1f, SoundClips = new List<AudioClip> { Clip(foley[ev]) } });
                }
                player.MainSounds = player.MainSounds ?? new List<BaseSoundPlayer_SoundElement>();

                string path = PrefabPath(ContainerName(b));
                AssetDatabase.DeleteAsset(path);
                PrefabUtility.SaveAsPrefabAsset(root, path);
                Debug.Log($"[Perks] Container prefab {path}: {player.AdditionalSounds.Count} sound events");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        // ---------- Materials ----------

        /// <summary>
        /// Standard (Specular setup) from Greyhound's maps, with the machines' approved vibrancy on the colour map. The
        /// glass is see-through and glossy, the drink inside nearly opaque, the label and cap solid.
        /// </summary>
        private static Material BuildMaterial(string name, string modelDir, Bottle b)
        {
            // The glass material is shared by every bottle, but each tints its own
            string assetName = name.EndsWith("_glass") ? name + "_" + b.Id : name;
            string path = $"{RgPaths.Materials}/{assetName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard (Specular setup)")) { name = assetName };
                AssetDatabase.CreateAsset(mat, path);
            }
            string folder = Path.Combine(modelDir, "_images", name);
            string albedo = FindBySuffix(folder, "_c.png"), normal = FindBySuffix(folder, "_n.png"), spec = FindBySuffix(folder, "_s.png");
            mat.SetTexture("_MainTex", albedo != null ? PerkImporter.VibrantCopy(albedo) : null);
            mat.color = Color.white;
            mat.SetTexture("_BumpMap", normal != null ? PerkImporter.Import(normal, TextureImporterType.NormalMap, false) : null);
            if (normal != null) mat.EnableKeyword("_NORMALMAP"); else mat.DisableKeyword("_NORMALMAP");
            if (spec != null)
            {
                mat.SetTexture("_SpecGlossMap", PerkImporter.SpecGloss(spec, FindBySuffix(folder, "_g.png")));
                mat.SetFloat("_GlossMapScale", 1f);
                mat.EnableKeyword("_SPECGLOSSMAP");
            }
            else
            {
                mat.SetTexture("_SpecGlossMap", null);
                mat.DisableKeyword("_SPECGLOSSMAP");
                mat.SetColor("_SpecColor", new Color(0.2f, 0.2f, 0.2f));
                mat.SetFloat("_Glossiness", 0.5f);
            }
            Opaque(mat);
            mat.SetTexture("_EmissionMap", null);
            mat.SetColor("_EmissionColor", Color.black);
            mat.DisableKeyword("_EMISSION");
            if (name.EndsWith("_glass"))
            {
                // See-through, and always drawn after the drink inside (both see-through, they swapped order frame to
                // frame and the bottle flickered between full and empty)
                mat.SetColor("_SpecColor", new Color(0.25f, 0.22f, 0.22f));
                mat.SetFloat("_Glossiness", 0.9f);
                mat.color = b.Glass ?? Tint(FindBySuffix(Path.Combine(modelDir, "_images", WaterName(modelDir)), "_c.png"), 0.55f);
                Transparent(mat, 0.45f);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent + 10;
            }
            else if (name.Contains("_water_"))
            {
                // The drink: solid, so the bottle always reads full, with a faint glow of its own colour like the original's
                mat.SetColor("_SpecColor", new Color(0.2f, 0.18f, 0.18f));
                mat.SetFloat("_Glossiness", 0.8f);
                // Deeper red than the original's bright map
                mat.color = b.Liquid ?? new Color(0.78f, 0.78f, 0.78f);
                if (mat.GetTexture("_MainTex") is Texture2D drink)
                {
                    mat.SetTexture("_EmissionMap", drink);
                    mat.SetColor("_EmissionColor", new Color(0.12f, 0.03f, 0.03f));
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    mat.EnableKeyword("_EMISSION");
                }
            }
            else if (name.Contains("_metal_"))
            {
                // The neck foil and cap: the original's map is a very dark red that read as black; lifted to the perk's red
                mat.color = b.Cap ?? Lift(albedo);
            }
            else if (name.Contains("_paper_"))
            {
                // The label has see-through gaps (its alpha)
                mat.SetFloat("_Mode", 1f);
                mat.SetFloat("_Cutoff", 0.5f);
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
            }
            EditorUtility.SetDirty(mat);
            return mat;
        }

        /// <summary>The material folder of the bottle's drink in this model (mtl_..._water_&lt;perk&gt;).</summary>
        private static string WaterName(string modelDir)
        {
            string images = Path.Combine(modelDir, "_images");
            return Directory.Exists(images) ? Path.GetFileName(Directory.GetDirectories(images).FirstOrDefault(d => d.Contains("_water_")) ?? "") : "";
        }

        private static Color Average(string pngPath)
        {
            if (pngPath == null)
                return Color.gray;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(File.ReadAllBytes(pngPath));
            double r = 0, g = 0, bl = 0, n = 0;
            foreach (Color c in tex.GetPixels())
            {
                if (c.a < 0.5f) continue;
                r += c.r; g += c.g; bl += c.b; n++;
            }
            Object.DestroyImmediate(tex);
            return n == 0 ? Color.gray : new Color((float)(r / n), (float)(g / n), (float)(bl / n));
        }

        /// <summary>The map's average colour, saturated up and scaled so its brightest channel is <paramref name="peak"/>.</summary>
        private static Color Tint(string pngPath, float peak)
        {
            Color.RGBToHSV(Average(pngPath), out float h, out float s, out _);
            Color c = Color.HSVToRGB(h, Mathf.Clamp01(s * 1.1f), 1f) * peak;
            c.a = 1f;
            return c;
        }

        /// <summary>A colour multiplier that brings a dark map (the cap and foil) up to a readable brightness.</summary>
        private static Color Lift(string mapPath)
        {
            Color avg = Average(mapPath);
            float luma = Mathf.Max(0.05f, avg.r * 0.3f + avg.g * 0.59f + avg.b * 0.11f);
            float k = Mathf.Clamp(0.35f / luma, 1f, 2.5f);
            return new Color(k, k, k);
        }

        private static void Opaque(Material mat)
        {
            mat.SetFloat("_Mode", 0f);
            mat.SetOverrideTag("RenderType", "");
            mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
            mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetInt("_ZWrite", 1);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.DisableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = -1;
        }

        /// <summary>
        /// Standard's "Fade" blend, as the Ray Gun's glass: the game renders it. "Transparent" (premultiplied) came out
        /// black in game (inspect view) while looking right in the editor.
        /// </summary>
        private static void Transparent(Material mat, float alpha)
        {
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
            c.a = alpha;
            mat.color = c;
        }

        private static string FindBySuffix(string dir, string suffix) =>
            Directory.Exists(dir) ? Directory.GetFiles(dir, "*" + suffix).FirstOrDefault(f => !Path.GetFileName(f).StartsWith("$")) : null;

        // ---------- Copying the vanilla item's component values ----------

        /// <summary>The dumped fields of the vanilla component of this class whose owner path contains <paramref name="ownerPart"/>.</summary>
        private static object ComponentFields(string cls, string ownerPart, string file = null)
        {
            file = file ?? $"{RgPaths.BaseGun}_components.json";
            object all = MiniJson.Parse(File.ReadAllText(RgPaths.Reference(file)));
            object entry = all.Arr().FirstOrDefault(c => c.Str("class") == cls && c.Str("owner").Contains(ownerPart));
            if (entry == null)
                throw new InvalidOperationException($"No {cls} on {ownerPart} in {file}");
            return entry.Get("fields");
        }

        /// <summary>Vanilla rig paths are "Transform:item_..._anim.generated/a/b"; ours are the same below the root.</summary>
        private static Object ResolveInRig(string reference, Transform root)
        {
            if (reference == null || !reference.StartsWith("Transform:"))
                return null;
            string path = reference.Substring("Transform:".Length);
            int slash = path.IndexOf('/');
            if (slash < 0)
                return path.EndsWith(".generated") ? root : null;
            return root.Find(path.Substring(slash + 1));
        }

        /// <summary>
        /// Fills a stub's serialized fields from a dumped component: values by name, nested classes and lists
        /// recursively, object references through <paramref name="resolve"/> (unknown ones become null).
        /// </summary>
        public static void CopyFields(object target, object json, Func<string, Object> resolve)
        {
            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = json.Get(field.Name);
                if (value == null && !(json is Dictionary<string, object> d && d.ContainsKey(field.Name)))
                    continue;
                field.SetValue(target, Convert(value, field.FieldType, resolve));
            }
        }

        private static object Convert(object value, Type type, Func<string, Object> resolve)
        {
            if (typeof(Object).IsAssignableFrom(type))
            {
                Object o = value is Dictionary<string, object> r ? resolve(r.Str("ref")) : null;
                return o != null && type.IsInstanceOfType(o) ? o : null;
            }
            if (value == null)
                return type.IsValueType ? Activator.CreateInstance(type) : null;
            if (type == typeof(string)) return value as string ?? "";
            if (type == typeof(float)) return (float)(double)value;
            if (type == typeof(double)) return (double)value;
            if (type == typeof(int)) return (int)(long)(double)value;
            if (type == typeof(uint)) return (uint)(long)(double)value;
            if (type == typeof(long)) return (long)(double)value;
            if (type == typeof(short)) return (short)(double)value;
            if (type == typeof(ushort)) return (ushort)(double)value;
            if (type == typeof(byte)) return value is bool b ? (byte)(b ? 1 : 0) : (byte)(double)value;
            if (type == typeof(sbyte)) return (sbyte)(double)value;
            if (type == typeof(bool)) return value is bool bb ? bb : (double)value != 0;
            if (type == typeof(Vector2)) return new Vector2(value.Num("x"), value.Num("y"));
            if (type == typeof(Vector3)) return new Vector3(value.Num("x"), value.Num("y"), value.Num("z"));
            if (type == typeof(Vector4)) return new Vector4(value.Num("x"), value.Num("y"), value.Num("z"), value.Num("w"));
            if (type == typeof(Quaternion)) return new Quaternion(value.Num("x"), value.Num("y"), value.Num("z"), value.Num("w"));
            if (type == typeof(Color)) return new Color(value.Num("r"), value.Num("g"), value.Num("b"), value.Num("a"));
            if (type == typeof(LayerMask)) return (LayerMask)value.Int("m_Bits");
            if (type == typeof(AnimationCurve))
            {
                var keys = (value.Get("m_Curve") as List<object> ?? new List<object>())
                    .Select(k => new Keyframe(k.Num("time"), k.Num("value"), k.Num("inSlope"), k.Num("outSlope"), k.Num("inWeight"), k.Num("outWeight"))).ToArray();
                return new AnimationCurve(keys);
            }
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                Type element = type.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(type);
                if (value is List<object> items)
                    foreach (object item in items)
                        list.Add(Convert(item, element, resolve));
                return list;
            }
            if (type.IsClass || (type.IsValueType && !type.IsPrimitive))
            {
                object instance = Activator.CreateInstance(type);
                if (value is Dictionary<string, object>)
                    CopyFields(instance, value, resolve);
                return instance;
            }
            return null;
        }
    }
}
