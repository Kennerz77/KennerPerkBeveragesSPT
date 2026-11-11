using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PerkBeverages
{
    /// <summary>Plain 16-bit PCM WAVs beside the plugin (sounds\<name>.wav), built into clips once.</summary>
    internal static class PerkSounds
    {
        private static readonly Dictionary<string, AudioClip> Clips = new Dictionary<string, AudioClip>();

        public static AudioClip Loose(string name)
        {
            if (Clips.TryGetValue(name, out AudioClip clip))
                return clip;
            string path = Path.Combine(PerkPlugin.Dir, "sounds", name + ".wav");
            try
            {
                clip = File.Exists(path) ? Build(name, File.ReadAllBytes(path)) : null;
                if (clip == null)
                    PerkPlugin.Log.LogWarning($"Sound missing or unreadable: {path}");
            }
            catch (Exception e)
            {
                PerkPlugin.Log.LogWarning($"Sound {path} failed: {e.Message}");
            }
            return Clips[name] = clip;
        }

        private static AudioClip Build(string name, byte[] bytes)
        {
            int channels = BitConverter.ToInt16(bytes, 22), rate = BitConverter.ToInt32(bytes, 24);
            // Walk the chunks to "data" (there can be others before it)
            for (int at = 12; at + 8 <= bytes.Length;)
            {
                string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
                int size = BitConverter.ToInt32(bytes, at + 4);
                if (id == "data")
                {
                    int count = Math.Min(size, bytes.Length - at - 8) / 2;
                    var samples = new float[count];
                    for (int i = 0; i < count; i++)
                        samples[i] = BitConverter.ToInt16(bytes, at + 8 + i * 2) / 32768f;
                    AudioClip clip = AudioClip.Create(name, count / channels, channels, rate, false);
                    clip.SetData(samples, 0);
                    return clip;
                }
                at += 8 + size + (size & 1);
            }
            return null;
        }
    }
}
