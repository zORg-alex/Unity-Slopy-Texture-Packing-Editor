using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace TexturePackEditor
{
    public sealed class TexturePackPixelSession : IDisposable
    {
        private readonly TexturePackSourceSet _sources;
        private readonly int _width;
        private readonly int _height;
        private readonly Dictionary<Texture2D, Color[]> _pixels = new();

        public int Width => _width;
        public int Height => _height;

        public TexturePackPixelSession(TexturePackSourceSet sources, int width, int height)
        {
            _sources = sources;
            _width = width;
            _height = height;
        }

        public Color Sample(TexturePackNode node, int pixel)
        {
            Texture2D texture = _sources.Resolve(node);
            if (texture == null) return Color.black;
            if (!_pixels.TryGetValue(texture, out var colors))
            {
                colors = ReadLinear(texture, _width, _height);
                _pixels.Add(texture, colors);
            }
            return colors[pixel];
        }

        public void Dispose() => _pixels.Clear();

        private static Color[] ReadLinear(Texture2D source, int width, int height)
        {
            RenderTexture temporary = RenderTexture.GetTemporary(width, height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, temporary);
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
    }

    public static class TexturePackProcessor
    {
        private const string GeneratedMarker = "TexturePackEditorRecipe:";

        public static float Evaluate(IReadOnlyList<TexturePackNode> nodes, int lastNode,
            TexturePackPixelSession session, int pixel)
        {
            Color value = Color.black;
            bool scalar = true;
            int end = Mathf.Min(lastNode, nodes.Count - 1);
            for (int index = 0; index <= end; index++)
            {
                TexturePackNode node = nodes[index];
                switch (node.type)
                {
                    case TexturePackNodeType.Sample:
                    {
                        Color sample = session.Sample(node, pixel);
                        int mask = node.channelMask & 15;
                        if (IsSingleBit(mask))
                        {
                            float component = mask switch { 1 => sample.r, 2 => sample.g, 4 => sample.b, _ => sample.a };
                            value = new Color(component, component, component, component);
                            scalar = true;
                        }
                        else
                        {
                            value = new Color((mask & 1) != 0 ? sample.r : 0, (mask & 2) != 0 ? sample.g : 0,
                                (mask & 4) != 0 ? sample.b : 0, (mask & 8) != 0 ? sample.a : 0);
                            scalar = false;
                        }
                        break;
                    }
                    case TexturePackNodeType.Desaturate:
                    {
                        float weight = node.normalizeLuminance
                            ? Mathf.Max(0.0001f, node.luminanceRed + node.luminanceGreen + node.luminanceBlue)
                            : 1;
                        float luminance = (value.r * node.luminanceRed + value.g * node.luminanceGreen +
                                           value.b * node.luminanceBlue) / weight;
                        value = new Color(luminance, luminance, luminance, luminance);
                        scalar = true;
                        break;
                    }
                    case TexturePackNodeType.Levels:
                        value = Map(value, component => Levels(component, node));
                        break;
                    case TexturePackNodeType.Noise:
                    {
                        int x = pixel % session.Width;
                        int y = pixel / session.Width;
                        float noise = FractalNoise((x + 0.5f) / session.Width,
                            (y + 0.5f) / session.Height, node.noiseScale, node.noiseSeed);
                        value = node.noiseMode switch
                        {
                            TexturePackNoiseMode.Multiply => value * Mathf.Lerp(1, noise * 2, node.noiseAmount),
                            TexturePackNoiseMode.Blend => Color.Lerp(value, new Color(noise, noise, noise, noise), node.noiseAmount),
                            _ => value + new Color(1, 1, 1, 1) * ((noise - 0.5f) * node.noiseAmount)
                        };
                        break;
                    }
                    case TexturePackNodeType.Invert:
                        value = Color.white - value;
                        break;
                    case TexturePackNodeType.MultiplyAdd:
                        value = value * node.multiply + new Color(node.add, node.add, node.add, node.add);
                        break;
                    case TexturePackNodeType.Constant:
                        value = new Color(node.constant, node.constant, node.constant, node.constant);
                        scalar = true;
                        break;
                }
            }
            return Mathf.Clamp01(scalar ? value.r : value.r);
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

        public static Texture2D CreateOutputPreview(TexturePackRecipe recipe, TexturePackPixelSession session)
        {
            var preview = new Texture2D(session.Width, session.Height, TextureFormat.RGBA32, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            var pixels = new Color32[session.Width * session.Height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = EvaluatePixel(recipe, session, i, false);
            preview.SetPixels32(pixels);
            preview.Apply(false, true);
            return preview;
        }

        public static string Bake(TexturePackRecipe recipe, Texture2D anchor, string suffix)
        {
            if (recipe == null || anchor == null) throw new ArgumentNullException();
            string recipePath = AssetDatabase.GetAssetPath(recipe);
            if (string.IsNullOrEmpty(recipePath)) throw new InvalidOperationException("Save the recipe asset before generating.");
            suffix = SanitizeSuffix(suffix);
            if (string.IsNullOrEmpty(suffix)) throw new InvalidOperationException("Safe output suffix cannot be empty.");

            recipe.EnsureChannels();
            TexturePackSourceSet sources = TexturePackSourceSet.Detect(anchor);
            Texture2D outputBase = sources.ResolveRole(recipe.outputBaseRole);
            if (outputBase == null) throw new InvalidOperationException("Output-base role is unresolved: " + recipe.outputBaseRole);
            string basePath = AssetDatabase.GetAssetPath(outputBase);
            string candidate = Path.Combine(Path.GetDirectoryName(basePath) ?? "Assets",
                    Path.GetFileNameWithoutExtension(basePath) + suffix + ".tga").Replace('\\', '/');
            string recipeGuid = AssetDatabase.AssetPathToGUID(recipePath);
            string outputPath = ResolveSafeOutputPath(candidate, recipe.lastGeneratedPath, recipeGuid);
            var outputImporter = AssetImporter.GetAtPath(basePath) as TextureImporter;
            bool outputSrgb = outputImporter != null && outputImporter.sRGBTexture;
            int width = outputBase.width;
            int height = outputBase.height;

            using (var session = new TexturePackPixelSession(sources, width, height))
            {
                var pixels = new Color32[width * height];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = EvaluatePixel(recipe, session, i, outputSrgb);
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
                texture.SetPixels32(pixels);
                texture.Apply(false, false);
                File.WriteAllBytes(Path.GetFullPath(outputPath), texture.EncodeToTGA());
                UnityEngine.Object.DestroyImmediate(texture);
            }

            AssetDatabase.ImportAsset(outputPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
            ConfigureImporter(outputPath, outputImporter, recipeGuid);
            recipe.lastGeneratedPath = outputPath;
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssetIfDirty(recipe);
            return outputPath;
        }

        private static Color32 EvaluatePixel(TexturePackRecipe recipe, TexturePackPixelSession session, int pixel,
            bool encodeSrgb)
        {
            float r = Evaluate(recipe.channels[0].nodes, recipe.channels[0].nodes.Count - 1, session, pixel);
            float g = Evaluate(recipe.channels[1].nodes, recipe.channels[1].nodes.Count - 1, session, pixel);
            float b = Evaluate(recipe.channels[2].nodes, recipe.channels[2].nodes.Count - 1, session, pixel);
            float a = Evaluate(recipe.channels[3].nodes, recipe.channels[3].nodes.Count - 1, session, pixel);
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

        private static string ResolveSafeOutputPath(string candidate, string previous, string recipeGuid)
        {
            if (!File.Exists(Path.GetFullPath(candidate))) return candidate;
            var importer = AssetImporter.GetAtPath(candidate);
            bool owned = string.Equals(previous, candidate, StringComparison.OrdinalIgnoreCase) &&
                         importer != null && importer.userData == GeneratedMarker + recipeGuid;
            return owned ? candidate : AssetDatabase.GenerateUniqueAssetPath(candidate);
        }

        private static string SanitizeSuffix(string suffix)
        {
            foreach (char invalid in Path.GetInvalidFileNameChars()) suffix = suffix.Replace(invalid.ToString(), "");
            return suffix.Trim();
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
