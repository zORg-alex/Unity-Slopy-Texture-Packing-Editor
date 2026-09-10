using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TexturePackEditor
{
    public enum TexturePackNodeType { Sample, Desaturate, Levels, Noise, Invert, MultiplyAdd, Constant }
    public enum TexturePackSourceKind { DetectedRole, ManualTexture }
    public enum TexturePackNoiseMode { Add, Multiply, Blend }

    [Serializable]
    public sealed class TexturePackNode
    {
        public string id = Guid.NewGuid().ToString("N");
        public TexturePackNodeType type;
        public bool expanded = true;

        public TexturePackSourceKind sourceKind;
        public string sourceRole;
        public Texture2D manualTexture;
        public int channelMask = 1;

        public float luminanceRed = 0.2126f;
        public float luminanceGreen = 0.7152f;
        public float luminanceBlue = 0.0722f;
        public bool normalizeLuminance = true;

        public float inputBlack;
        public float inputWhite = 1;
        public float gamma = 1;
        public float outputBlack;
        public float outputWhite = 1;

        public float noiseScale = 8;
        public int noiseSeed = 1;
        public float noiseAmount = 0.15f;
        public TexturePackNoiseMode noiseMode = TexturePackNoiseMode.Add;

        public float multiply = 1;
        public float add;
        public float constant;

        public TexturePackNode Clone()
        {
            return new TexturePackNode
            {
                type = type, expanded = expanded, sourceKind = sourceKind, sourceRole = sourceRole,
                manualTexture = manualTexture, channelMask = channelMask,
                luminanceRed = luminanceRed, luminanceGreen = luminanceGreen,
                luminanceBlue = luminanceBlue, normalizeLuminance = normalizeLuminance,
                inputBlack = inputBlack, inputWhite = inputWhite, gamma = gamma,
                outputBlack = outputBlack, outputWhite = outputWhite,
                noiseScale = noiseScale, noiseSeed = noiseSeed, noiseAmount = noiseAmount,
                noiseMode = noiseMode, multiply = multiply, add = add, constant = constant
            };
        }

        public static TexturePackNode Create(TexturePackNodeType nodeType)
        {
            return new TexturePackNode { type = nodeType };
        }
    }

    [Serializable]
    public sealed class TexturePackChannelStack
    {
        public List<TexturePackNode> nodes = new();
    }

    public sealed class TexturePackRecipe : ScriptableObject
    {
        public string outputBaseRole;
        public List<Texture2D> manualSources = new();
        public List<TexturePackChannelStack> channels = new();
        [HideInInspector] public string lastGeneratedPath;

        public void EnsureChannels()
        {
            while (channels.Count < 4) channels.Add(new TexturePackChannelStack());
            while (channels.Count > 4) channels.RemoveAt(channels.Count - 1);
            foreach (var channel in channels) channel.nodes ??= new List<TexturePackNode>();
        }

        public void ResetTo(TexturePackSourceSet sourceSet)
        {
            EnsureChannels();
            outputBaseRole = sourceSet.AnchorRole;
            for (int channel = 0; channel < 4; channel++)
            {
                channels[channel].nodes.Clear();
                channels[channel].nodes.Add(new TexturePackNode
                {
                    type = TexturePackNodeType.Sample,
                    sourceKind = TexturePackSourceKind.DetectedRole,
                    sourceRole = sourceSet.AnchorRole,
                    channelMask = 1 << channel
                });
            }
        }

        public void CopyFrom(TexturePackRecipe source)
        {
            outputBaseRole = source.outputBaseRole;
            manualSources = new List<Texture2D>(source.manualSources);
            channels = source.channels.Select(stack => new TexturePackChannelStack
            {
                nodes = stack.nodes.Select(node => node.Clone()).ToList()
            }).ToList();
            lastGeneratedPath = null;
            EnsureChannels();
        }
    }

    public sealed class TexturePackSourceSet
    {
        public string Folder { get; private set; }
        public string Prefix { get; private set; }
        public string AnchorRole { get; private set; }
        public IReadOnlyDictionary<string, Texture2D> Detected => _detected;
        private readonly Dictionary<string, Texture2D> _detected = new(StringComparer.OrdinalIgnoreCase);

        public static TexturePackSourceSet Detect(Texture2D anchor)
        {
            var set = new TexturePackSourceSet();
            if (anchor == null) return set;
            string path = AssetDatabase.GetAssetPath(anchor);
            set.Folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string stem = Path.GetFileNameWithoutExtension(path);
            int separator = stem.LastIndexOf('_');
            set.Prefix = separator > 0 ? stem.Substring(0, separator) : stem;
            set.AnchorRole = separator > 0 ? stem.Substring(separator + 1) : stem;
            if (string.IsNullOrEmpty(set.Folder)) return set;
            string rolePrefix = set.Prefix + "_";
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { set.Folder }))
            {
                string candidatePath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(Path.GetDirectoryName(candidatePath)?.Replace('\\', '/'), set.Folder,
                        StringComparison.OrdinalIgnoreCase)) continue;
                string candidateStem = Path.GetFileNameWithoutExtension(candidatePath);
                if (!candidateStem.StartsWith(rolePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string role = candidateStem.Substring(rolePrefix.Length);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(candidatePath);
                if (texture != null && !set._detected.ContainsKey(role)) set._detected.Add(role, texture);
            }
            if (!set._detected.ContainsKey(set.AnchorRole)) set._detected[set.AnchorRole] = anchor;
            return set;
        }

        public Texture2D Resolve(TexturePackNode node)
        {
            if (node.sourceKind == TexturePackSourceKind.ManualTexture) return node.manualTexture;
            return node.sourceRole != null && _detected.TryGetValue(node.sourceRole, out var texture) ? texture : null;
        }

        public Texture2D ResolveRole(string role)
        {
            return role != null && _detected.TryGetValue(role, out var texture) ? texture : null;
        }

        public string[] SortedRoles() => _detected.Keys.OrderBy(role => role).ToArray();
    }
}
