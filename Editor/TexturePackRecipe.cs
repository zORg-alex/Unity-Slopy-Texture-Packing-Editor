using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
        public string sourceRoleId;
        public Texture2D manualTexture;
        public int channelMask = 1;
        public bool sampleDesaturate;

        public float desaturateAmount = 1;
        public float desaturateBlack;
        public float desaturateWhite = 1;
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

        [NonSerialized] internal Color[] preparedPixels;
        [NonSerialized] internal Color32[] preparedCompactPixels;
        [NonSerialized] internal Color preparedFallback = Color.black;

        public TexturePackNode Clone(bool preserveId = false)
        {
            return new TexturePackNode
            {
                id = preserveId ? id : Guid.NewGuid().ToString("N"),
                type = type, expanded = expanded, sourceKind = sourceKind, sourceRole = sourceRole,
                sourceRoleId = sourceRoleId,
                manualTexture = manualTexture, channelMask = channelMask,
                sampleDesaturate = sampleDesaturate,
                desaturateAmount = desaturateAmount, desaturateBlack = desaturateBlack,
                desaturateWhite = desaturateWhite, luminanceRed = luminanceRed, luminanceGreen = luminanceGreen,
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
        public bool expanded;
        public List<TexturePackNode> nodes = new();
    }

    [Serializable]
    public sealed class TexturePackOutput
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name;
        public string outputBaseRole;
        public string outputBaseRoleId;
        [Tooltip("Optional file name without an extension. Empty uses the base texture name.")]
        public string outputFileName;
        public int emptySlotSize;
        public List<TexturePackChannelStack> channels = new();
        [HideInInspector] public string lastGeneratedPath;

        public void EnsureChannels()
        {
            while (channels.Count < 4) channels.Add(new TexturePackChannelStack());
            while (channels.Count > 4) channels.RemoveAt(channels.Count - 1);
            foreach (var channel in channels) channel.nodes ??= new List<TexturePackNode>();
        }

        public static TexturePackOutput Create(TexturePackSourceSet sourceSet, string role, string displayName = null)
        {
            var output = new TexturePackOutput
            {
                name = string.IsNullOrWhiteSpace(displayName) ? role : displayName,
                outputBaseRole = role,
                outputBaseRoleId = role
            };
            output.EnsureChannels();
            for (int channel = 0; channel < 4; channel++)
            {
                if (sourceSet != null && sourceSet.CanCreateEmptyRole(role))
                {
                    float value = role == "normal" ? (channel < 2 ? .5f : 1f) :
                        role == "maskmap" ? (channel == 1 || channel == 2 ? 1f : 0f) : (channel == 3 ? 1f : 0f);
                    output.channels[channel].nodes.Add(new TexturePackNode { type = TexturePackNodeType.Constant, constant = value });
                    continue;
                }
                output.channels[channel].nodes.Add(new TexturePackNode
                {
                    type = TexturePackNodeType.Sample,
                    sourceKind = TexturePackSourceKind.DetectedRole,
                    sourceRole = role ?? sourceSet?.AnchorRole,
                    sourceRoleId = role ?? sourceSet?.AnchorRole,
                    channelMask = 1 << channel
                });
            }
            return output;
        }

        public TexturePackOutput Clone(bool preserveIds = false)
        {
            return new TexturePackOutput
            {
                id = preserveIds ? id : Guid.NewGuid().ToString("N"),
                name = name,
                outputBaseRole = outputBaseRole,
                outputBaseRoleId = outputBaseRoleId,
                outputFileName = outputFileName,
                emptySlotSize = emptySlotSize,
                channels = channels.Select(stack => new TexturePackChannelStack
                {
                    expanded = stack.expanded,
                    nodes = stack.nodes.Select(node => node.Clone(preserveIds)).ToList()
                }).ToList()
            };
        }
    }

    public sealed class TexturePackRecipe : ScriptableObject
    {
        private const int CurrentRoleSchema = 1;
        public List<TexturePackOutput> outputs = new();
        public string outputBaseRole;
        public List<Texture2D> manualSources = new();
        public List<TexturePackRoleOverride> roleOverrides = new();
        public List<TexturePackChannelStack> channels = new();
        [HideInInspector] public string lastGeneratedPath;
        [HideInInspector] public int roleSchemaVersion;

        public void EnsureChannels()
        {
            EnsureOutputs();
        }

        public void EnsureOutputs()
        {
            outputs ??= new List<TexturePackOutput>();
            channels ??= new List<TexturePackChannelStack>();
            manualSources ??= new List<Texture2D>();
            roleOverrides ??= new List<TexturePackRoleOverride>();
            if (outputs.Count == 0)
            {
                var migrated = new TexturePackOutput
                {
                    name = string.IsNullOrEmpty(outputBaseRole) ? "Output" : outputBaseRole,
                    outputBaseRole = outputBaseRole,
                    channels = channels ?? new List<TexturePackChannelStack>(),
                    lastGeneratedPath = lastGeneratedPath
                };
                outputs.Add(migrated);
            }
            foreach (var output in outputs) output.EnsureChannels();
            if (roleSchemaVersion < CurrentRoleSchema)
            {
                foreach (TexturePackOutput output in outputs)
                {
                    output.outputBaseRoleId = EffectiveOutputRole(output);
                    foreach (TexturePackNode node in output.channels.SelectMany(channel => channel.nodes))
                        if (node.type == TexturePackNodeType.Sample &&
                            node.sourceKind == TexturePackSourceKind.DetectedRole)
                            node.sourceRoleId = EffectiveRole(node);
                }
                roleSchemaVersion = CurrentRoleSchema;
                if (EditorUtility.IsPersistent(this)) EditorUtility.SetDirty(this);
            }
            // Keep the original fields synchronized for recipes created by the first version.
            outputBaseRole = outputs[0].outputBaseRole;
            channels = outputs[0].channels;
            lastGeneratedPath = outputs[0].lastGeneratedPath;
        }

        public void ResetTo(TexturePackSourceSet sourceSet)
        {
            outputs.Clear();
            if (sourceSet.HasBoundTarget)
            {
                foreach (string role in sourceSet.TerrainLayer != null ? TexturePackTerrainBinding.Roles : sourceSet.SortedRoles())
                    if (sourceSet.ResolveRole(role) != null)
                        outputs.Add(TexturePackOutput.Create(sourceSet, role,
                            TexturePackProjectSettings.instance.DisplayName(role)));
            }
            else outputs.Add(TexturePackOutput.Create(sourceSet, sourceSet.AnchorRole));
            EnsureOutputs();
        }

        public void CopyFrom(TexturePackRecipe source)
        {
            source.EnsureOutputs();
            outputs = source.outputs.Select(output => output.Clone()).ToList();
            manualSources = new List<Texture2D>(source.manualSources);
            roleOverrides = source.roleOverrides.Select(rule => new TexturePackRoleOverride
            {
                roleId = rule.roleId, mode = rule.mode, matchMode = rule.matchMode, expression = rule.expression
            }).ToList();
            roleSchemaVersion = source.roleSchemaVersion;
            EnsureOutputs();
        }

        public TexturePackRoleOverride RoleOverride(string roleId)
        {
            roleOverrides ??= new List<TexturePackRoleOverride>();
            return roleOverrides.FirstOrDefault(rule =>
                string.Equals(rule.roleId, roleId, StringComparison.OrdinalIgnoreCase));
        }

        public string EffectiveRole(TexturePackNode node)
            => !string.IsNullOrEmpty(node.sourceRoleId) ? node.sourceRoleId :
                TexturePackRoleMatcher.ResolveLegacy(node.sourceRole, this);

        public string EffectiveOutputRole(TexturePackOutput output)
        {
            if (!string.IsNullOrEmpty(output.outputBaseRoleId)) return output.outputBaseRoleId;
            string namedRole = TexturePackRoleMatcher.ResolveLegacy(output.name, this);
            return !string.IsNullOrEmpty(namedRole) ? namedRole :
                TexturePackRoleMatcher.ResolveLegacy(output.outputBaseRole, this);
        }
    }

    public sealed class TexturePackSourceSet
    {
        private const string SuffixKey = "TexturePackEditor.SafeOutputSuffix";
        public string Folder { get; private set; }
        public string Prefix { get; private set; }
        public string AnchorRole { get; private set; }
        public TerrainLayer TerrainLayer { get; private set; }
        public Material Material { get; private set; }
        public Shader MaterialShader { get; private set; }
        public bool HasBoundTarget => TerrainLayer != null || Material != null;
        private readonly Dictionary<string, Texture2D> _boundAssignments = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _materialProperties = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, Texture2D> Detected => _detected;
        public IReadOnlyDictionary<string, List<Texture2D>> Conflicts => _conflicts;
        public bool HasUnresolvedConflicts => _conflicts.Keys.Any(role => !_detected.ContainsKey(role));
        public IReadOnlyList<Texture2D> Unmatched => _unmatched;
        public IReadOnlyList<string> Errors => _errors;
        private readonly Dictionary<string, Texture2D> _detected = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Texture2D>> _conflicts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Texture2D> _unmatched = new();
        private readonly List<string> _errors = new();
        private TexturePackRecipe _recipe;

        public Texture2D BoundAssignment(string role)
            => role != null && _boundAssignments.TryGetValue(role, out Texture2D texture) ? texture : null;

        public string MaterialProperty(string role)
            => role != null && _materialProperties.TryGetValue(role, out string property) ? property : null;

        public string BoundSlot(string role)
            => TerrainLayer != null && TexturePackTerrainBinding.Supports(role) ? role : MaterialProperty(role);

        public bool CanCreateEmptyRole(string role)
            => ResolveRole(role) == null && BoundSlot(role) != null && ResolveRole("basemap") != null &&
               !_conflicts.ContainsKey(role);

        public int[] EmptySlotSizes()
        {
            Texture2D basemap = ResolveRole("basemap");
            if (basemap == null) return Array.Empty<int>();
            int limit = Mathf.Min(basemap.width, basemap.height);
            var sizes = new List<int>();
            for (int size = 1; size <= limit; size *= 2) sizes.Add(size);
            return sizes.ToArray();
        }

        public Color EmptyRoleSample(TexturePackNode node)
        {
            string role = _recipe != null ? _recipe.EffectiveRole(node) : node.sourceRoleId ?? node.sourceRole;
            if (node.sourceKind != TexturePackSourceKind.DetectedRole || !CanCreateEmptyRole(role)) return Color.black;
            return role == "normal" ? new Color(.5f, .5f, 1, 1) :
                role == "maskmap" ? new Color(0, 1, 1, 0) : Color.black;
        }

        public int EmptySlotResolution(TexturePackOutput output)
        {
            int[] sizes = EmptySlotSizes();
            if (sizes.Length == 0) throw new InvalidOperationException("Assign a base map before creating an empty texture slot.");
            int requested = output.emptySlotSize > 0 ? output.emptySlotSize : sizes[^1];
            return sizes.Last(size => size <= Mathf.Max(1, requested));
        }

        public string BoundOverwritePath(string role)
        {
            Texture2D assigned = BoundAssignment(role);
            return TexturePackProcessor.IsGeneratedTexture(assigned) ? AssetDatabase.GetAssetPath(assigned) : null;
        }

        public static TexturePackSourceSet FromTerrainLayer(TerrainLayer layer, TexturePackRecipe recipe = null)
        {
            var set = new TexturePackSourceSet { _recipe = recipe, TerrainLayer = layer };
            if (layer == null) return set;
            string layerPath = AssetDatabase.GetAssetPath(layer);
            set.Folder = string.IsNullOrEmpty(layerPath) ? "Assets" :
                Path.GetDirectoryName(layerPath)?.Replace('\\', '/');
            set.Prefix = layer.name;
            foreach (string role in TexturePackTerrainBinding.Roles)
            {
                Texture2D assigned = TexturePackTerrainBinding.GetTexture(layer, role);
                set._boundAssignments[role] = assigned;
                if (assigned == null || TexturePackProcessor.IsEmptySlotTexture(assigned)) continue;
                // The layer slots are authoritative, independent of filenames and role matching rules.
                set._detected[role] = TexturePackProcessor.OriginalTexture(assigned);
                set.AnchorRole ??= role;
            }
            return set;
        }

        public static TexturePackSourceSet FromMaterial(Material material, TexturePackRecipe recipe = null,
            IReadOnlyList<TexturePackMaterialSlot> overrides = null)
        {
            var set = new TexturePackSourceSet { _recipe = recipe, Material = material,
                MaterialShader = material == null ? null : material.shader };
            if (material == null) return set;
            string path = AssetDatabase.GetAssetPath(material);
            set.Folder = string.IsNullOrEmpty(path) ? "Assets" : Path.GetDirectoryName(path)?.Replace('\\', '/');
            set.Prefix = material.name;
            string[] properties = TexturePackMaterialBinding.TextureProperties(material);
            foreach (TexturePackRoleDefinition role in TexturePackProjectSettings.instance.Roles)
            {
                TexturePackMaterialSlot mapping = overrides?.FirstOrDefault(slot =>
                    string.Equals(slot.roleId, role.id, StringComparison.OrdinalIgnoreCase));
                string property;
                if (mapping != null)
                {
                    property = mapping.propertyName;
                    if (string.IsNullOrEmpty(property)) continue;
                    if (!properties.Contains(property))
                    {
                        set._errors.Add("Material slot no longer exists: " + property);
                        continue;
                    }
                }
                else
                {
                    string[] candidates = properties.Where(name => material.GetTexture(name) is Texture2D texture &&
                        !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(texture)) &&
                        string.Equals(TexturePackMaterialBinding.RoleForProperty(name, recipe), role.id,
                            StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (candidates.Length == 0)
                        candidates = properties.Where(name =>
                            (material.GetTexture(name) == null || material.GetTexture(name) is Texture2D defaultTexture &&
                                string.IsNullOrEmpty(AssetDatabase.GetAssetPath(defaultTexture))) &&
                            string.Equals(TexturePackMaterialBinding.RoleForProperty(name, recipe), role.id,
                                StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (candidates.Length == 0) continue;
                    if (candidates.Length > 1)
                    {
                        set._conflicts[role.id] = candidates.Select(name => material.GetTexture(name) as Texture2D).ToList();
                        set._errors.Add(role.name + " matches " + string.Join(", ", candidates) +
                            ". Choose its texture slot under Material slots.");
                        continue;
                    }
                    property = candidates[0];
                }
                set._materialProperties[role.id] = property;
                var assigned = material.GetTexture(property) as Texture2D;
                set._boundAssignments[role.id] = assigned;
                if (assigned == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(assigned)) ||
                    TexturePackProcessor.IsEmptySlotTexture(assigned)) continue;
                set._detected[role.id] = TexturePackProcessor.OriginalTexture(assigned);
                if (set.AnchorRole == null || role.id == "basemap") set.AnchorRole = role.id;
            }
            return set;
        }

        public static TexturePackSourceSet Detect(Texture2D anchor, TexturePackRecipe recipe = null)
        {
            var set = new TexturePackSourceSet();
            set._recipe = recipe;
            if (anchor == null) return set;
            anchor = TexturePackProcessor.OriginalTexture(anchor);
            string path = AssetDatabase.GetAssetPath(anchor);
            set.Folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string stem = Path.GetFileNameWithoutExtension(path);
            string suffix = EditorPrefs.GetString(SuffixKey, "_Wet");
            if (IsGenerated(path, stem, suffix))
            {
                string originalStem = stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                    ? stem.Substring(0, stem.Length - suffix.Length) : stem;
                Texture2D original = FindExactTexture(set.Folder, originalStem);
                if (original != null)
                {
                    anchor = original;
                    path = AssetDatabase.GetAssetPath(anchor);
                    stem = Path.GetFileNameWithoutExtension(path);
                }
            }
            stem = StripResolutionSuffix(stem);
            int separator = stem.LastIndexOf('_');
            set.Prefix = separator > 0 ? stem.Substring(0, separator) : stem;
            string anchorRawRole = separator > 0 ? stem.Substring(separator + 1) : stem;
            set.AnchorRole = TexturePackRoleMatcher.Match(anchorRawRole, recipe, out string anchorError);
            if (!string.IsNullOrEmpty(anchorError)) set._errors.Add(anchor.name + ": " + anchorError);
            if (string.IsNullOrEmpty(set.Folder)) return set;
            string rolePrefix = set.Prefix + "_";
            var assignments = new Dictionary<string, List<Texture2D>>(StringComparer.OrdinalIgnoreCase);
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { set.Folder }))
            {
                string candidatePath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(Path.GetDirectoryName(candidatePath)?.Replace('\\', '/'), set.Folder,
                        StringComparison.OrdinalIgnoreCase)) continue;
                string candidateStem = Path.GetFileNameWithoutExtension(candidatePath);
                if (IsGenerated(candidatePath, candidateStem, suffix)) continue;
                candidateStem = StripResolutionSuffix(candidateStem);
                if (!candidateStem.StartsWith(rolePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string rawRole = candidateStem.Substring(rolePrefix.Length);
                var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(candidatePath);
                if (texture == null) continue;
                string role = TexturePackRoleMatcher.Match(rawRole, recipe, out string error);
                if (!string.IsNullOrEmpty(error)) set._errors.Add(texture.name + ": " + error);
                if (string.IsNullOrEmpty(role))
                {
                    set._unmatched.Add(texture);
                    continue;
                }
                if (!assignments.TryGetValue(role, out List<Texture2D> textures))
                    assignments.Add(role, textures = new List<Texture2D>());
                textures.Add(texture);
            }
            foreach (var pair in assignments)
            {
                if (pair.Value.Count == 1) set._detected.Add(pair.Key, pair.Value[0]);
                else
                {
                    set._conflicts.Add(pair.Key, pair.Value);
                }
            }
            return set;
        }

        public Texture2D Resolve(TexturePackNode node)
        {
            if (node.sourceKind == TexturePackSourceKind.ManualTexture) return node.manualTexture;
            string role = _recipe != null ? _recipe.EffectiveRole(node) :
                !string.IsNullOrEmpty(node.sourceRoleId) ? node.sourceRoleId :
                TexturePackRoleMatcher.ResolveLegacy(node.sourceRole, null);
            return role != null && _detected.TryGetValue(role, out var texture) ? texture : null;
        }

        public Texture2D ResolveRole(string role)
        {
            return role != null && _detected.TryGetValue(role, out var texture) ? texture : null;
        }

        public string[] SortedRoles() => _detected.Keys.OrderBy(role => role).ToArray();

        // Normalize only matching names; keep asset paths and generated-file checks untouched.
        private static string StripResolutionSuffix(string stem)
            => Regex.Replace(stem, @"_[1-9][0-9]*[kK]$", string.Empty);

        private static bool IsGenerated(string path, string stem, string suffix)
        {
            var importer = AssetImporter.GetAtPath(path);
            return (!string.IsNullOrEmpty(importer?.userData) &&
                    importer.userData.StartsWith(TexturePackProcessor.GeneratedMarker, StringComparison.Ordinal)) ||
                   (!string.IsNullOrEmpty(suffix) && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        private static Texture2D FindExactTexture(string folder, string stem)
        {
            if (string.IsNullOrEmpty(folder)) return null;
            foreach (string guid in AssetDatabase.FindAssets(stem + " t:Texture2D", new[] { folder }))
            {
                string candidate = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileNameWithoutExtension(candidate), stem,
                        StringComparison.OrdinalIgnoreCase))
                    return AssetDatabase.LoadAssetAtPath<Texture2D>(candidate);
            }
            return null;
        }
    }
}
