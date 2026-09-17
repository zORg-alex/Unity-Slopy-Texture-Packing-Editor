using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace TexturePackEditor
{
    public sealed class TexturePackBakePlan : IDisposable
    {
        internal TexturePackOutput output;
        internal TexturePackPixelSession session;
        internal string outputPath;
        internal string sourcePath;
        internal string templatePath;
        internal bool emptySlot;
        internal string recipeGuid;
        internal bool encodeSrgb;
        internal TerrainLayer terrainLayer;
        internal string terrainRole;
        internal Texture2D terrainAssignment;
        internal Material material;
        internal Shader materialShader;
        internal string materialProperty;
        internal Texture2D materialAssignment;

        public string OutputPath => outputPath;
        public int Width => session.Width;
        public int Height => session.Height;

        public void Dispose()
        {
            session?.Dispose();
            session = null;
        }
    }

    public sealed class TexturePackPixelSession : IDisposable
    {
        private readonly TexturePackSourceSet _sources;
        private readonly int _width;
        private readonly int _height;
        private readonly Rect _uvRect;
        private readonly bool _compact;
        private readonly Dictionary<Texture2D, Color[]> _pixels = new();
        private readonly Dictionary<Texture2D, Color32[]> _compactPixels = new();
        private readonly List<TexturePackNode> _preparedNodes = new();
        private bool _prepared;

        public int Width => _width;
        public int Height => _height;
        public Vector2 PixelUv(int pixel) => new(
            _uvRect.x + (pixel % _width + .5f) / _width * _uvRect.width,
            _uvRect.y + (pixel / _width + .5f) / _height * _uvRect.height);

        public TexturePackPixelSession(TexturePackSourceSet sources, int width, int height)
            : this(sources, width, height, new Rect(0, 0, 1, 1))
        {
        }

        public TexturePackPixelSession(TexturePackSourceSet sources, int width, int height, Rect uvRect,
            bool compact = false)
        {
            _sources = sources;
            _width = width;
            _height = height;
            _uvRect = uvRect;
            _compact = compact;
        }

        public Color Sample(TexturePackNode node, int pixel)
        {
            if (_prepared)
            {
                if (_compact)
                {
                    Color32[] prepared = node.preparedCompactPixels;
                    return prepared != null ? prepared[pixel] : node.preparedFallback;
                }
                Color[] preparedFloat = node.preparedPixels;
                return preparedFloat != null ? preparedFloat[pixel] : node.preparedFallback;
            }
            Texture2D texture = _sources.Resolve(node);
            if (texture == null) return _sources.EmptyRoleSample(node);
            if (_compact)
            {
                if (!_compactPixels.TryGetValue(texture, out var compactColors))
                {
                    compactColors = ReadLinearCompact(texture, _width, _height, _uvRect);
                    _compactPixels.Add(texture, compactColors);
                }
                return compactColors[pixel];
            }
            if (!_pixels.TryGetValue(texture, out var colors))
            {
                colors = ReadLinear(texture, _width, _height, _uvRect);
                _pixels.Add(texture, colors);
            }
            return colors[pixel];
        }

        /// <summary>Captures Unity textures on the main thread, after which evaluation is thread-safe.</summary>
        public void Prepare(IEnumerable<TexturePackNode> nodes)
        {
            foreach (TexturePackNode node in nodes)
            {
                if (node.type == TexturePackNodeType.Height)
                {
                    if (node.heightInput != null) continue;
                    Texture2D source = _sources.Resolve(node);
                    if (source == null) throw new InvalidOperationException("Choose a source texture for the Height node.");
                    int limit = Mathf.Clamp(Mathf.ClosestPowerOfTwo(node.heightResolution), 128, 2048);
                    float scale = Math.Min(1f, limit / (float)Math.Max(source.width, source.height));
                    int width = Math.Max(2, Mathf.ClosestPowerOfTwo(Mathf.RoundToInt(source.width * scale)));
                    int height = Math.Max(2, Mathf.ClosestPowerOfTwo(Mathf.RoundToInt(source.height * scale)));
                    string path = AssetDatabase.GetAssetPath(source);
                    node.heightInput = new TexturePackHeight.Input
                    {
                        width = width, height = height,
                        key = (string.IsNullOrEmpty(path) ? Guid.NewGuid().ToString("N") : path + ":" + AssetDatabase.GetAssetDependencyHash(path)) + ":" + width + ":" + height
                    };
                    _preparedNodes.Add(node);
                    // Center/strength/blend changes only remap the solved image. Pin the cached
                    // array for this job before skipping capture, even if the shared cache evicts it.
                    if (!TexturePackHeight.TryCaptureCachedMap(node, node.heightInput))
                    {
                        node.heightInput.backend = node.heightMode == TexturePackHeightMode.DeepBump ? TexturePackDeepBump.Capture() : null;
                        node.heightInput.pixels = ReadLinearCompact(source, width, height, new Rect(0, 0, 1, 1));
                    }
                    continue;
                }
                if (node.type != TexturePackNodeType.Sample || node.preparedPixels != null ||
                    node.preparedCompactPixels != null) continue;
                Texture2D texture = _sources.Resolve(node);
                if (texture == null)
                {
                    node.preparedFallback = _sources.EmptyRoleSample(node);
                    continue;
                }
                if (_compact)
                {
                    if (!_compactPixels.TryGetValue(texture, out var compactColors))
                    {
                        compactColors = ReadLinearCompact(texture, _width, _height, _uvRect);
                        _compactPixels.Add(texture, compactColors);
                    }
                    node.preparedCompactPixels = compactColors;
                    _preparedNodes.Add(node);
                    continue;
                }
                if (!_pixels.TryGetValue(texture, out var colors))
                {
                    colors = ReadLinear(texture, _width, _height, _uvRect);
                    _pixels.Add(texture, colors);
                }
                node.preparedPixels = colors;
                _preparedNodes.Add(node);
            }
            _prepared = true;
        }

        public void PrepareHeights(IEnumerable<TexturePackNode> nodes, CancellationToken cancellationToken)
        {
            foreach (var node in nodes)
            {
                if (node.type != TexturePackNodeType.Height || node.preparedHeight != null) continue;
                float[] map = TexturePackHeight.Build(node, cancellationToken);
                var input = node.heightInput;
                var sampled = new float[_width * _height];
                for (int pixel = 0; pixel < sampled.Length; pixel++)
                {
                    if ((pixel & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    Vector2 uv = PixelUv(pixel);
                    sampled[pixel] = TexturePackHeight.Sample(map, input.width, input.height, uv.x, uv.y, node.heightSeamless);
                }
                node.preparedHeight = sampled;
            }
        }

        public void Dispose()
        {
            foreach (TexturePackNode node in _preparedNodes)
            {
                node.preparedPixels = null;
                node.preparedCompactPixels = null;
                node.heightInput?.backend?.Dispose();
                node.heightInput = null;
                node.preparedHeight = null;
            }
            _preparedNodes.Clear();
            _pixels.Clear();
            _compactPixels.Clear();
            _prepared = false;
        }

        private static void BlitSource(Texture2D source, RenderTexture destination, Rect uvRect)
        {
            var importer = AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(source)) as TextureImporter;
            if (importer == null || importer.textureType != TextureImporterType.NormalMap)
            {
                Graphics.Blit(source, destination, new Vector2(uvRect.width, uvRect.height),
                    new Vector2(uvRect.x, uvRect.y));
                return;
            }
            // Imported normals may use DXT5nm/BC5 packing. Convert them back to RGB before exporting.
            Shader shader = Shader.Find("Hidden/TexturePackEditor/ReadNormal");
            if (shader == null) throw new InvalidOperationException("Texture Pack Editor normal capture shader is missing.");
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                material.SetVector("_UvRect", new Vector4(uvRect.x, uvRect.y, uvRect.width, uvRect.height));
                Graphics.Blit(source, destination, material);
            }
            finally { UnityEngine.Object.DestroyImmediate(material); }
        }

        private static Color[] ReadLinear(Texture2D source, int width, int height, Rect uvRect)
        {
            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                BlitSource(source, temporary, uvRect);
                RenderTexture.active = temporary;
                var readable = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply(false, false);
                Color[] result = readable.GetPixels();
                UnityEngine.Object.DestroyImmediate(readable);
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private static Color32[] ReadLinearCompact(Texture2D source, int width, int height, Rect uvRect)
        {
            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                BlitSource(source, temporary, uvRect);
                RenderTexture.active = temporary;
                var readable = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                readable.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                readable.Apply(false, false);
                Color32[] result = readable.GetPixels32();
                UnityEngine.Object.DestroyImmediate(readable);
                return result;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }
    }

    public static class TexturePackProcessor
    {
        internal const string GeneratedMarker = "TexturePackEditorRecipe:";

        public static float Evaluate(IReadOnlyList<TexturePackNode> nodes, int lastNode,
            TexturePackPixelSession session, int pixel)
        {
            Color value = Color.black;
            bool scalar = true;
            int end = Mathf.Min(lastNode, nodes.Count - 1);
            for (int index = 0; index <= end; index++)
                value = ApplyNode(nodes[index], value, scalar, session, pixel, out scalar);
            return Mathf.Clamp01(value.r);
        }

        public static Color ApplyNode(TexturePackNode node, Color value, bool scalar,
            TexturePackPixelSession session, int pixel, out bool outputScalar)
        {
            outputScalar = scalar;
            switch (node.type)
            {
                case TexturePackNodeType.Height:
                    if (node.preparedHeight == null) throw new InvalidOperationException("Height map has not been prepared.");
                    float height = Mathf.Clamp01((node.preparedHeight[pixel] - .5f) * node.heightStrength + node.heightCenter);
                    return Blend(node, value, scalar, new Color(height, height, height, height), true, out outputScalar);
                case TexturePackNodeType.Sample:
                {
                    Color sample = session.Sample(node, pixel);
                    int mask = node.channelMask & 15;
                    if (IsSingleBit(mask))
                    {
                        float component = mask switch { 1 => sample.r, 2 => sample.g, 4 => sample.b, _ => sample.a };
                        outputScalar = true;
                        Color selected = new(component, component, component, component);
                        if (node.sampleDesaturate) selected = Desaturate(node, selected, true, out outputScalar);
                        return Blend(node, value, scalar, selected, outputScalar, out outputScalar);
                    }
                    outputScalar = false;
                    Color channels = new((mask & 1) != 0 ? sample.r : 0, (mask & 2) != 0 ? sample.g : 0,
                        (mask & 4) != 0 ? sample.b : 0, (mask & 8) != 0 ? sample.a : 0);
                    if (node.sampleDesaturate) channels = Desaturate(node, channels, false, out outputScalar);
                    return Blend(node, value, scalar, channels, outputScalar, out outputScalar);
                }
                case TexturePackNodeType.Desaturate:
                    return Desaturate(node, value, scalar, out outputScalar);
                case TexturePackNodeType.Levels:
                    return Map(value, component => Levels(component, node));
                case TexturePackNodeType.Noise:
                {
                    Vector2 uv = session.PixelUv(pixel);
                    float noise = FractalNoise(uv.x, uv.y, node.noiseScale, node.noiseSeed);
                    return node.noiseMode switch
                    {
                        TexturePackNoiseMode.Multiply => value * Mathf.Lerp(1, noise * 2, node.noiseAmount),
                        TexturePackNoiseMode.Blend => Color.Lerp(value, new Color(noise, noise, noise, noise), node.noiseAmount),
                        _ => value + Color.white * ((noise - 0.5f) * node.noiseAmount)
                    };
                }
                case TexturePackNodeType.Invert:
                    return Color.white - value;
                case TexturePackNodeType.MultiplyAdd:
                    return value * node.multiply + new Color(node.add, node.add, node.add, node.add);
                case TexturePackNodeType.Constant:
                    outputScalar = true;
                    return Blend(node, value, scalar, new Color(node.constant, node.constant, node.constant, node.constant),
                        true, out outputScalar);
                default:
                    return value;
            }
        }

        public static Color Blend(TexturePackNode node, Color input, bool inputScalar, Color layer, bool layerScalar,
            out bool outputScalar)
        {
            float amount = Mathf.Clamp01(node.blendAmount);
            outputScalar = amount <= 0 ? inputScalar : node.blendMode == TexturePackBlendMode.Replace && amount >= 1
                ? layerScalar : inputScalar && layerScalar;
            if (amount <= 0) return input;
            if (node.blendMode == TexturePackBlendMode.Replace && amount >= 1) return layer;
            if (inputScalar) input = new Color(input.r, input.r, input.r, input.r);
            Color result = default;
            for (int channel = 0; channel < 4; channel++)
            {
                float a = input[channel], b = layer[channel];
                float blended = node.blendMode switch
                {
                    TexturePackBlendMode.Add => a + b,
                    TexturePackBlendMode.Subtract => a - b,
                    TexturePackBlendMode.Multiply => a * b,
                    TexturePackBlendMode.Screen => 1 - (1 - a) * (1 - b),
                    TexturePackBlendMode.Overlay => a <= .5f ? 2 * a * b : 1 - 2 * (1 - a) * (1 - b),
                    TexturePackBlendMode.Minimum => Mathf.Min(a, b),
                    TexturePackBlendMode.Maximum => Mathf.Max(a, b),
                    _ => b
                };
                result[channel] = Mathf.LerpUnclamped(a, blended, amount);
            }
            return result;
        }

        private static Color Desaturate(TexturePackNode node, Color value, bool scalar, out bool outputScalar)
        {
            float weight = node.normalizeLuminance
                ? Mathf.Max(.0001f, node.luminanceRed + node.luminanceGreen + node.luminanceBlue) : 1;
            float luminance = (value.r * node.luminanceRed + value.g * node.luminanceGreen + value.b * node.luminanceBlue) / weight;
            luminance = Mathf.Lerp(node.desaturateBlack, node.desaturateWhite, luminance);
            outputScalar = scalar || node.desaturateAmount >= .999f;
            return Color.Lerp(value, new Color(luminance, luminance, luminance, luminance), node.desaturateAmount);
        }

        public static Texture2D CreateChannelPreview(TexturePackChannelStack stack, int lastNode,
            TexturePackPixelSession session)
        {
            var nodes = stack.nodes.Take(lastNode + 1).ToArray();
            session.Prepare(nodes);
            session.PrepareHeights(nodes, default);
            var preview = new Texture2D(session.Width, session.Height, TextureFormat.RGBA32, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[session.Width * session.Height];
            for (int i = 0; i < pixels.Length; i++)
            {
                byte value = ToByte(Evaluate(stack.nodes, lastNode, session, i));
                pixels[i] = new Color32(value, value, value, 255);
            }
            preview.SetPixels32(pixels);
            preview.Apply(false, true);
            return preview;
        }

        public static Texture2D CreateOutputPreview(TexturePackOutput output, TexturePackPixelSession session)
        {
            var nodes = output.channels.SelectMany(channel => channel.nodes).ToArray();
            session.Prepare(nodes);
            session.PrepareHeights(nodes, default);
            var preview = new Texture2D(session.Width, session.Height, TextureFormat.RGBA32, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[session.Width * session.Height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = EvaluatePixel(output, session, i, false);
            preview.SetPixels32(pixels);
            preview.Apply(false, true);
            return preview;
        }

        public static TexturePackBakePlan PrepareBake(TexturePackRecipe recipe, int outputIndex,
            Texture2D anchor, string suffix, TexturePackOutput outputSnapshot = null,
            TexturePackSourceSet sourceSnapshot = null)
        {
            if (recipe == null || (anchor == null && sourceSnapshot == null)) throw new ArgumentNullException();
            string recipePath = AssetDatabase.GetAssetPath(recipe);
            if (string.IsNullOrEmpty(recipePath)) throw new InvalidOperationException("Save the recipe asset before generating.");
            suffix = SanitizeSuffix(suffix);
            if (string.IsNullOrEmpty(suffix)) throw new InvalidOperationException("Safe output suffix cannot be empty.");

            recipe.EnsureOutputs();
            if (outputSnapshot == null && (outputIndex < 0 || outputIndex >= recipe.outputs.Count))
                throw new ArgumentOutOfRangeException(nameof(outputIndex));
            TexturePackOutput output = outputSnapshot ?? recipe.outputs[outputIndex];
            TexturePackSourceSet sources = sourceSnapshot ?? TexturePackSourceSet.Detect(anchor, recipe);
            string outputRole = recipe.EffectiveOutputRole(output);
            Texture2D outputBase = sources.ResolveRole(outputRole);
            bool emptySlot = outputBase == null && sources.CanCreateEmptyRole(outputRole);
            if (emptySlot) outputBase = sources.ResolveRole("basemap");
            if (outputBase == null) throw new InvalidOperationException("Output-base role is unresolved: " +
                                                                        TexturePackProjectSettings.instance.DisplayName(outputRole));
            string basePath = AssetDatabase.GetAssetPath(outputBase);
            string candidate = OutputCandidate(recipe, output, sources, suffix);
            string recipeGuid = AssetDatabase.AssetPathToGUID(recipePath);
            if (HasOutputConflict(candidate, recipeGuid, output.id, sources.BoundOverwritePath(outputRole)))
                throw new InvalidOperationException("Output file already belongs to another output: " + candidate +
                    ". Choose a different File name and generate again.");
            string outputPath = candidate;
            var outputImporter = AssetImporter.GetAtPath(basePath) as TextureImporter;
            bool outputSrgb = emptySlot ? outputRole == "basemap" || outputRole == "specular" :
                outputImporter != null && outputImporter.sRGBTexture;
            int width = emptySlot ? sources.EmptySlotResolution(output) : outputBase.width;
            int height = emptySlot ? width : outputBase.height;
            var session = new TexturePackPixelSession(sources, width, height, new Rect(0, 0, 1, 1), true);
            try
            {
                TexturePackOutput snapshot = output.Clone(true);
                session.Prepare(snapshot.channels.SelectMany(channel => channel.nodes));
                return new TexturePackBakePlan
                {
                    output = snapshot,
                    session = session,
                    outputPath = outputPath,
                    sourcePath = emptySlot ? null : basePath,
                    templatePath = basePath,
                    emptySlot = emptySlot,
                    recipeGuid = recipeGuid,
                    terrainLayer = sources.TerrainLayer,
                    terrainRole = outputRole,
                    terrainAssignment = sources.BoundAssignment(outputRole),
                    material = sources.Material,
                    materialShader = sources.MaterialShader,
                    materialProperty = sources.MaterialProperty(outputRole),
                    materialAssignment = sources.BoundAssignment(outputRole),
                    encodeSrgb = outputSrgb
                };
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public static void ExecuteBake(TexturePackBakePlan plan, Action<float> reportProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (plan == null || plan.session == null) throw new ArgumentNullException(nameof(plan));
            plan.session.PrepareHeights(plan.output.channels.SelectMany(channel => channel.nodes), cancellationToken);
            int width = plan.Width;
            int height = plan.Height;
            var pixels = new Color32[checked(width * height)];
            int completedRows = 0;
            int reportedRows = 0;
            int reportInterval = Math.Max(1, height / 200);
            object progressLock = new();
            int processorCount = Environment.ProcessorCount;
            int reservedProcessors = Math.Max(1, processorCount / 4);
            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(1, processorCount - reservedProcessors)
            };
            Parallel.For(0, height, options, y =>
            {
                int start = y * width;
                int end = start + width;
                for (int pixel = start; pixel < end; pixel++)
                    pixels[pixel] = EvaluatePixel(plan.output, plan.session, pixel, plan.encodeSrgb);
                int complete = Interlocked.Increment(ref completedRows);
                if (complete == height || complete % reportInterval == 0)
                {
                    lock (progressLock)
                    {
                        if (complete > reportedRows)
                        {
                            reportedRows = complete;
                            reportProgress?.Invoke(complete / (float)height * .86f);
                        }
                    }
                }
            });
            cancellationToken.ThrowIfCancellationRequested();
            WriteTga(plan.outputPath, pixels, width, height, reportProgress, cancellationToken);
            reportProgress?.Invoke(1);
        }

        public static string CompleteBake(TexturePackBakePlan plan, TexturePackRecipe recipe)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            AssetDatabase.ImportAsset(plan.outputPath,
                ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            ConfigureImporter(plan.outputPath, AssetImporter.GetAtPath(plan.templatePath) as TextureImporter,
                plan.recipeGuid, plan.output.id, plan.sourcePath, plan.emptySlot ? plan.terrainRole : null);
            if (plan.terrainLayer != null)
                TexturePackTerrainBinding.Apply(plan.terrainLayer, plan.terrainRole,
                    AssetDatabase.LoadAssetAtPath<Texture2D>(plan.outputPath), plan.terrainAssignment);
            if (plan.materialProperty != null)
                TexturePackMaterialBinding.Apply(plan.material, plan.materialShader, plan.materialProperty,
                    AssetDatabase.LoadAssetAtPath<Texture2D>(plan.outputPath), plan.materialAssignment);
            recipe.EnsureOutputs();
            TexturePackOutput destination = recipe.outputs.FirstOrDefault(output => output.id == plan.output.id);
            if (destination != null) destination.lastGeneratedPath = plan.outputPath;
            recipe.EnsureOutputs();
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssetIfDirty(recipe);
            return plan.outputPath;
        }

        public static string Bake(TexturePackRecipe recipe, int outputIndex, Texture2D anchor, string suffix)
        {
            using TexturePackBakePlan plan = PrepareBake(recipe, outputIndex, anchor, suffix);
            ExecuteBake(plan);
            return CompleteBake(plan, recipe);
        }

        private static void WriteTga(string assetPath, Color32[] pixels, int width, int height,
            Action<float> reportProgress, CancellationToken cancellationToken)
        {
            if (width > ushort.MaxValue || height > ushort.MaxValue)
                throw new InvalidOperationException("TGA dimensions cannot exceed 65535 pixels.");
            string destination = Path.GetFullPath(assetPath);
            string temporary = destination + ".texture-pack-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 65536))
                {
                    byte[] header = new byte[18];
                    header[2] = 2;
                    header[12] = (byte)width;
                    header[13] = (byte)(width >> 8);
                    header[14] = (byte)height;
                    header[15] = (byte)(height >> 8);
                    header[16] = 32;
                    stream.Write(header, 0, header.Length);
                    var row = new byte[width * 4];
                    int progressInterval = Mathf.Max(1, height / 100);
                    for (int y = 0; y < height; y++)
                    {
                        if (y % progressInterval == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            reportProgress?.Invoke(.86f + y / (float)height * .13f);
                        }
                        int sourceOffset = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            Color32 color = pixels[sourceOffset + x];
                            int target = x * 4;
                            row[target] = color.b;
                            row[target + 1] = color.g;
                            row[target + 2] = color.r;
                            row[target + 3] = color.a;
                        }
                        stream.Write(row, 0, row.Length);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        public static Color32 EvaluatePixel(TexturePackOutput output, TexturePackPixelSession session, int pixel,
            bool encodeSrgb)
        {
            float r = Evaluate(output.channels[0].nodes, output.channels[0].nodes.Count - 1, session, pixel);
            float g = Evaluate(output.channels[1].nodes, output.channels[1].nodes.Count - 1, session, pixel);
            float b = Evaluate(output.channels[2].nodes, output.channels[2].nodes.Count - 1, session, pixel);
            float a = Evaluate(output.channels[3].nodes, output.channels[3].nodes.Count - 1, session, pixel);
            if (encodeSrgb)
            {
                r = Mathf.LinearToGammaSpace(r);
                g = Mathf.LinearToGammaSpace(g);
                b = Mathf.LinearToGammaSpace(b);
            }
            return new Color32(ToByte(r), ToByte(g), ToByte(b), ToByte(a));
        }

        private static void ConfigureImporter(string path, TextureImporter template, string recipeGuid, string outputId,
            string sourcePath, string emptyRole = null)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            if (template != null)
            {
                importer.textureType = template.textureType;
                importer.sRGBTexture = template.sRGBTexture;
                importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.alphaIsTransparency = template.alphaIsTransparency;
                importer.mipmapEnabled = template.mipmapEnabled;
                importer.wrapMode = template.wrapMode;
                importer.filterMode = template.filterMode;
                importer.anisoLevel = template.anisoLevel;
                importer.textureCompression = template.textureCompression;
                importer.maxTextureSize = template.maxTextureSize;
                importer.npotScale = template.npotScale;
            }
            if (emptyRole != null)
            {
                importer.textureType = emptyRole == "normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = emptyRole == "basemap" || emptyRole == "specular";
                importer.alphaIsTransparency = false;
            }
            // Keep the original source GUID so terrain bindings can regenerate without compounding edits.
            string originalGuid = emptyRole != null ? "empty" : path == sourcePath ? GeneratedSourceGuid(importer.userData) :
                string.IsNullOrEmpty(sourcePath) ? string.Empty : AssetDatabase.AssetPathToGUID(sourcePath);
            importer.userData = GeneratedMarker + recipeGuid + ":" + outputId + ":" + originalGuid;
            importer.SaveAndReimport();
        }

        public static bool HasOutputConflict(string candidate, string recipeGuid, string outputId,
            string boundOverwritePath = null)
        {
            if (!File.Exists(Path.GetFullPath(candidate))) return false;
            var importer = AssetImporter.GetAtPath(candidate);
            if (importer == null) return true;
            if (string.Equals(candidate, boundOverwritePath, StringComparison.OrdinalIgnoreCase) &&
                IsGeneratedMarker(importer.userData)) return false;
            string owner = GeneratedMarker + recipeGuid + ":" + outputId;
            return importer.userData != owner &&
                   !(importer.userData ?? string.Empty).StartsWith(owner + ":", StringComparison.Ordinal);
        }

        public static string OutputCandidate(TexturePackRecipe recipe, TexturePackOutput output,
            TexturePackSourceSet sources, string suffix)
        {
            string reuse = sources.BoundOverwritePath(recipe.EffectiveOutputRole(output));
            if (!string.IsNullOrEmpty(reuse)) return reuse;
            suffix = SanitizeSuffix(suffix);
            if (string.IsNullOrEmpty(suffix)) throw new InvalidOperationException("Safe output suffix cannot be empty.");
            string role = recipe.EffectiveOutputRole(output);
            Texture2D texture = sources.ResolveRole(role);
            bool emptySlot = texture == null && sources.CanCreateEmptyRole(role);
            if (emptySlot) texture = sources.ResolveRole("basemap");
            if (texture == null) throw new InvalidOperationException("Output base is unresolved.");
            string path = AssetDatabase.GetAssetPath(texture);
            string name = string.IsNullOrWhiteSpace(output.outputFileName)
                ? emptySlot ? SanitizeFileName(sources.Prefix + "_" + role) : Path.GetFileNameWithoutExtension(path)
                : SanitizeFileName(output.outputFileName);
            return Path.Combine(Path.GetDirectoryName(path) ?? "Assets", name + suffix + ".tga").Replace('\\', '/');
        }

        private static bool IsGeneratedMarker(string marker)
            => !string.IsNullOrEmpty(marker) && marker.StartsWith(GeneratedMarker, StringComparison.Ordinal);

        public static bool IsGeneratedTexture(Texture2D texture)
            => texture != null && IsGeneratedMarker(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture))?.userData);

        public static bool IsEmptySlotTexture(Texture2D texture)
            => texture != null && GeneratedSourceGuid(AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(texture))?.userData) == "empty";

        private static string GeneratedSourceGuid(string marker)
        {
            if (!IsGeneratedMarker(marker)) return null;
            string[] fields = marker.Substring(GeneratedMarker.Length).Split(':');
            return fields.Length >= 3 ? fields[2] : null;
        }

        public static Texture2D OriginalTexture(Texture2D texture)
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (texture != null)
            {
                string path = AssetDatabase.GetAssetPath(texture);
                if (string.IsNullOrEmpty(path) || !visited.Add(path)) return texture;
                string marker = AssetImporter.GetAtPath(path)?.userData;
                if (!IsGeneratedMarker(marker)) return texture;
                string sourceGuid = GeneratedSourceGuid(marker);
                Texture2D original = string.IsNullOrEmpty(sourceGuid) ? null :
                    AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GUIDToAssetPath(sourceGuid));
                // Compatibility with older exports that only stored ownership and used the safe suffix.
                if (original == null)
                {
                    string suffix = EditorPrefs.GetString("TexturePackEditor.SafeOutputSuffix", "_Wet");
                    string stem = Path.GetFileNameWithoutExtension(path);
                    string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                    if (!string.IsNullOrEmpty(suffix) && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrEmpty(folder))
                    {
                        string originalStem = stem.Substring(0, stem.Length - suffix.Length);
                        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { folder }))
                        {
                            string candidate = AssetDatabase.GUIDToAssetPath(guid);
                            if (string.Equals(Path.GetDirectoryName(candidate)?.Replace('\\', '/'), folder,
                                    StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(Path.GetFileNameWithoutExtension(candidate), originalStem,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                original = AssetDatabase.LoadAssetAtPath<Texture2D>(candidate);
                                break;
                            }
                        }
                    }
                }
                if (original == null || visited.Contains(AssetDatabase.GetAssetPath(original))) return texture;
                texture = original;
            }
            return texture;
        }

        private static string SanitizeSuffix(string suffix)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) suffix = suffix.Replace(invalid.ToString(), "");
            return suffix.Trim();
        }

        private static string SanitizeFileName(string value)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
            value = value.Trim();
            return string.IsNullOrEmpty(value) ? "PackedTexture" : value;
        }

        private static bool IsSingleBit(int value) => value != 0 && (value & (value - 1)) == 0;
        private static byte ToByte(float value) => (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255);

        private static float Levels(float value, TexturePackNode node)
        {
            float range = Mathf.Max(0.0001f, node.inputWhite - node.inputBlack);
            float normalized = Mathf.Clamp01((value - node.inputBlack) / range);
            normalized = Mathf.Pow(normalized, 1f / Mathf.Max(0.01f, node.gamma));
            return Mathf.Lerp(node.outputBlack, node.outputWhite, normalized);
        }

        private static Color Map(Color color, Func<float, float> map)
            => new(map(color.r), map(color.g), map(color.b), map(color.a));

        private static float FractalNoise(float x, float y, float scale, int seed)
        {
            x *= Mathf.Max(0.01f, scale); y *= Mathf.Max(0.01f, scale);
            float result = 0, amplitude = 0.5714f, total = 0;
            for (int octave = 0; octave < 3; octave++)
            {
                result += ValueNoise(x, y, seed + octave * 1013) * amplitude;
                total += amplitude; amplitude *= 0.5f; x *= 2; y *= 2;
            }
            return result / total;
        }

        private static float ValueNoise(float x, float y, int seed)
        {
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float a = Hash(ix, iy, seed), b = Hash(ix + 1, iy, seed);
            float c = Hash(ix, iy + 1, seed), d = Hash(ix + 1, iy + 1, seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
        }

        private static float Hash(int x, int y, int seed)
        {
            unchecked
            {
                uint value = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                value = (value ^ (value >> 13)) * 1274126177u;
                return (value & 0x00ffffff) / 16777215f;
            }
        }
    }
}
