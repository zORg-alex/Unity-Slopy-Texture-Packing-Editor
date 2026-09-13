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
        internal string recipeGuid;
        internal int outputIndex;
        internal bool encodeSrgb;

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
                    return prepared != null ? prepared[pixel] : Color.black;
                }
                Color[] preparedFloat = node.preparedPixels;
                return preparedFloat != null ? preparedFloat[pixel] : Color.black;
            }
            Texture2D texture = _sources.Resolve(node);
            if (texture == null) return Color.black;
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
                if (node.type != TexturePackNodeType.Sample || node.preparedPixels != null ||
                    node.preparedCompactPixels != null) continue;
                Texture2D texture = _sources.Resolve(node);
                if (texture == null) continue;
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

        public void Dispose()
        {
            foreach (TexturePackNode node in _preparedNodes)
            {
                node.preparedPixels = null;
                node.preparedCompactPixels = null;
            }
            _preparedNodes.Clear();
            _pixels.Clear();
            _compactPixels.Clear();
            _prepared = false;
        }

        private static Color[] ReadLinear(Texture2D source, int width, int height, Rect uvRect)
        {
            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, temporary, new Vector2(uvRect.width, uvRect.height),
                    new Vector2(uvRect.x, uvRect.y));
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
                Graphics.Blit(source, temporary, new Vector2(uvRect.width, uvRect.height),
                    new Vector2(uvRect.x, uvRect.y));
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
                case TexturePackNodeType.Sample:
                {
                    Color sample = session.Sample(node, pixel);
                    int mask = node.channelMask & 15;
                    if (IsSingleBit(mask))
                    {
                        float component = mask switch { 1 => sample.r, 2 => sample.g, 4 => sample.b, _ => sample.a };
                        outputScalar = true;
                        return new Color(component, component, component, component);
                    }
                    outputScalar = false;
                    return new Color((mask & 1) != 0 ? sample.r : 0, (mask & 2) != 0 ? sample.g : 0,
                        (mask & 4) != 0 ? sample.b : 0, (mask & 8) != 0 ? sample.a : 0);
                }
                case TexturePackNodeType.Desaturate:
                {
                    float weight = node.normalizeLuminance
                        ? Mathf.Max(0.0001f, node.luminanceRed + node.luminanceGreen + node.luminanceBlue)
                        : 1;
                    float luminance = (value.r * node.luminanceRed + value.g * node.luminanceGreen +
                                       value.b * node.luminanceBlue) / weight;
                    luminance = Mathf.Lerp(node.desaturateBlack, node.desaturateWhite, luminance);
                    Color gray = new(luminance, luminance, luminance, luminance);
                    outputScalar = scalar || node.desaturateAmount >= 0.999f;
                    return Color.Lerp(value, gray, node.desaturateAmount);
                }
                case TexturePackNodeType.Levels:
                    return Map(value, component => Levels(component, node));
                case TexturePackNodeType.Noise:
                {
                    int x = pixel % session.Width;
                    int y = pixel / session.Width;
                    float noise = FractalNoise((x + 0.5f) / session.Width,
                        (y + 0.5f) / session.Height, node.noiseScale, node.noiseSeed);
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
                    return new Color(node.constant, node.constant, node.constant, node.constant);
                default:
                    return value;
            }
        }

        public static Texture2D CreateChannelPreview(TexturePackChannelStack stack, int lastNode,
            TexturePackPixelSession session)
        {
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
            Texture2D anchor, string suffix, TexturePackOutput outputSnapshot = null)
        {
            if (recipe == null || anchor == null) throw new ArgumentNullException();
            string recipePath = AssetDatabase.GetAssetPath(recipe);
            if (string.IsNullOrEmpty(recipePath)) throw new InvalidOperationException("Save the recipe asset before generating.");
            suffix = SanitizeSuffix(suffix);
            if (string.IsNullOrEmpty(suffix)) throw new InvalidOperationException("Safe output suffix cannot be empty.");

            recipe.EnsureOutputs();
            if (outputIndex < 0 || outputIndex >= recipe.outputs.Count)
                throw new ArgumentOutOfRangeException(nameof(outputIndex));
            TexturePackOutput output = outputSnapshot ?? recipe.outputs[outputIndex];
            TexturePackSourceSet sources = TexturePackSourceSet.Detect(anchor, recipe);
            string outputRole = recipe.EffectiveOutputRole(output);
            Texture2D outputBase = sources.ResolveRole(outputRole);
            if (outputBase == null) throw new InvalidOperationException("Output-base role is unresolved: " +
                                                                        TexturePackProjectSettings.instance.DisplayName(outputRole));
            string basePath = AssetDatabase.GetAssetPath(outputBase);
            string baseName = string.IsNullOrWhiteSpace(output.outputFileName)
                ? Path.GetFileNameWithoutExtension(basePath)
                : SanitizeFileName(output.outputFileName);
            string candidate = Path.Combine(Path.GetDirectoryName(basePath) ?? "Assets",
                    baseName + suffix + ".tga").Replace('\\', '/');
            string recipeGuid = AssetDatabase.AssetPathToGUID(recipePath);
            string outputPath = ResolveSafeOutputPath(candidate, recipeGuid);
            var outputImporter = AssetImporter.GetAtPath(basePath) as TextureImporter;
            bool outputSrgb = outputImporter != null && outputImporter.sRGBTexture;
            int width = outputBase.width;
            int height = outputBase.height;
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
                    sourcePath = basePath,
                    recipeGuid = recipeGuid,
                    outputIndex = outputIndex,
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
            ConfigureImporter(plan.outputPath, AssetImporter.GetAtPath(plan.sourcePath) as TextureImporter,
                plan.recipeGuid);
            recipe.EnsureOutputs();
            if (plan.outputIndex >= 0 && plan.outputIndex < recipe.outputs.Count)
                recipe.outputs[plan.outputIndex].lastGeneratedPath = plan.outputPath;
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

        private static void ConfigureImporter(string path, TextureImporter template, string recipeGuid)
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
            importer.userData = GeneratedMarker + recipeGuid;
            importer.SaveAndReimport();
        }

        private static string ResolveSafeOutputPath(string candidate, string recipeGuid)
        {
            if (!File.Exists(Path.GetFullPath(candidate))) return candidate;
            var importer = AssetImporter.GetAtPath(candidate);
            bool owned = importer != null && importer.userData == GeneratedMarker + recipeGuid;
            return owned ? candidate : AssetDatabase.GenerateUniqueAssetPath(candidate);
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
