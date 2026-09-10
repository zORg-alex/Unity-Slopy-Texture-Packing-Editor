using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace TexturePackEditor
{
    public sealed class TexturePackPreviewResult
    {
        public int revision;
        public int width;
        public int height;
        public readonly Dictionary<string, Color32[]> nodePixels = new();
        public readonly Dictionary<string, float[]> histograms = new();
        public Color32[] outputPixels;
    }

    /// <summary>Pure CPU work. Texture capture and Texture2D creation stay on Unity's main thread.</summary>
    public static class TexturePackPreviewWork
    {
        public static TexturePackPreviewResult Compute(int revision, TexturePackOutput output,
            TexturePackPixelSession session)
        {
            var result = new TexturePackPreviewResult
            {
                revision = revision,
                width = session.Width,
                height = session.Height,
                outputPixels = new Color32[session.Width * session.Height]
            };
            int pixelCount = result.outputPixels.Length;

            for (int channel = 0; channel < 4; channel++)
            {
                TexturePackChannelStack stack = output.channels[channel];
                for (int nodeIndex = 0; nodeIndex < stack.nodes.Count; nodeIndex++)
                {
                    TexturePackNode node = stack.nodes[nodeIndex];
                    var preview = new Color32[pixelCount];
                    int[] histogram = node.type == TexturePackNodeType.Levels ? new int[256] : null;
                    for (int pixel = 0; pixel < pixelCount; pixel++)
                    {
                        if (histogram != null)
                        {
                            float before = nodeIndex == 0 ? 0 :
                                TexturePackProcessor.Evaluate(stack.nodes, nodeIndex - 1, session, pixel);
                            histogram[Mathf.Clamp(Mathf.RoundToInt(before * 255), 0, 255)]++;
                        }
                        byte value = (byte)Mathf.RoundToInt(TexturePackProcessor.Evaluate(
                            stack.nodes, nodeIndex, session, pixel) * 255);
                        preview[pixel] = new Color32(value, value, value, 255);
                    }
                    result.nodePixels[node.id] = preview;
                    if (histogram != null) result.histograms[node.id] = NormalizeHistogram(histogram);
                }
            }

            for (int pixel = 0; pixel < pixelCount; pixel++)
                result.outputPixels[pixel] = TexturePackProcessor.EvaluatePixel(output, session, pixel, false);
            return result;
        }

        private static float[] NormalizeHistogram(int[] bins)
        {
            int peak = bins.Length == 0 ? 0 : bins.Max();
            var normalized = new float[bins.Length];
            if (peak == 0) return normalized;
            // Square root keeps sparse peaks readable without hiding the rest of the distribution.
            for (int i = 0; i < bins.Length; i++) normalized[i] = Mathf.Sqrt((float)bins[i] / peak);
            return normalized;
        }
    }
}
