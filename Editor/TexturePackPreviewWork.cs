using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    public sealed class TexturePackPreviewUpdate
    {
        public int revision;
        public int width;
        public int height;
        public string nodeId;
        public Color32[] pixels;
        public float[] histogram;
        public bool isOutput;
    }

    /// <summary>Pure CPU work. Texture capture and Texture2D creation stay on Unity's main thread.</summary>
    public static class TexturePackPreviewWork
    {
        public static TexturePackPreviewResult Compute(int revision, TexturePackOutput output,
            TexturePackPixelSession session, int channelMask = 15, Color32[] previousOutput = null,
            int priorityChannel = -1, string priorityNodeId = null,
            Action<TexturePackPreviewUpdate> publish = null, int thumbnailSize = 96,
            bool retainNodeResults = true, bool takeOwnershipPreviousOutput = false,
            CancellationToken cancellationToken = default)
        {
            var result = new TexturePackPreviewResult
            {
                revision = revision,
                width = session.Width,
                height = session.Height,
                outputPixels = previousOutput != null && previousOutput.Length == session.Width * session.Height
                    ? takeOwnershipPreviousOutput ? previousOutput : (Color32[])previousOutput.Clone()
                    : new Color32[session.Width * session.Height]
            };
            int pixelCount = result.outputPixels.Length;
            int[] channelOrder = Enumerable.Range(0, 4)
                .Where(channel => (channelMask & (1 << channel)) != 0)
                .OrderBy(channel => channel == priorityChannel ? 0 : 1).ToArray();

            foreach (int channel in channelOrder)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TexturePackChannelStack stack = output.channels[channel];
                var values = new Color[pixelCount];
                var scalar = new bool[pixelCount];
                Array.Fill(scalar, true);
                var deferred = new List<TexturePackPreviewUpdate>();
                bool prioritizeNode = !string.IsNullOrEmpty(priorityNodeId) &&
                                      stack.nodes.Any(node => node.id == priorityNodeId);
                for (int nodeIndex = 0; nodeIndex < stack.nodes.Count; nodeIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TexturePackNode node = stack.nodes[nodeIndex];
                    int[] histogram = node.type == TexturePackNodeType.Levels ? new int[256] : null;
                    for (int pixel = 0; pixel < pixelCount; pixel++)
                    {
                        if ((pixel & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        if (histogram != null) histogram[ToByte(values[pixel].r)]++;
                        values[pixel] = TexturePackProcessor.ApplyNode(node, values[pixel], scalar[pixel],
                            session, pixel, out scalar[pixel]);
                    }
                    int nodePreviewSize = node.id == priorityNodeId ? session.Width :
                        Mathf.Min(thumbnailSize, session.Width);
                    Color32[] preview = CreatePreview(values, scalar, session.Width, session.Height,
                        nodePreviewSize, nodePreviewSize);
                    if (retainNodeResults) result.nodePixels[node.id] = preview;
                    float[] normalizedHistogram = histogram == null ? null : NormalizeHistogram(histogram);
                    if (retainNodeResults && normalizedHistogram != null)
                        result.histograms[node.id] = normalizedHistogram;
                    var update = new TexturePackPreviewUpdate
                    {
                        revision = revision, width = nodePreviewSize, height = nodePreviewSize,
                        nodeId = node.id, pixels = preview, histogram = normalizedHistogram
                    };
                    if (node.id == priorityNodeId) publish?.Invoke(update);
                    else if (prioritizeNode && channel == priorityChannel) deferred.Add(update);
                    else publish?.Invoke(update);
                }
                foreach (TexturePackPreviewUpdate update in deferred) publish?.Invoke(update);
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    Color32 packed = result.outputPixels[pixel];
                    byte value = ToByte(values[pixel].r);
                    switch (channel)
                    {
                        case 0: packed.r = value; break;
                        case 1: packed.g = value; break;
                        case 2: packed.b = value; break;
                        case 3: packed.a = value; break;
                    }
                    result.outputPixels[pixel] = packed;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            publish?.Invoke(new TexturePackPreviewUpdate
            {
                revision = revision, width = result.width, height = result.height,
                pixels = result.outputPixels, isOutput = true
            });
            return result;
        }

        private static Color32[] CreatePreview(Color[] values, bool[] scalar, int sourceWidth,
            int sourceHeight, int width, int height)
        {
            var preview = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                int sourceY = Mathf.Min(sourceHeight - 1, (int)((y + .5f) * sourceHeight / height));
                for (int x = 0; x < width; x++)
                {
                    int sourceX = Mathf.Min(sourceWidth - 1, (int)((x + .5f) * sourceWidth / width));
                    int source = sourceY * sourceWidth + sourceX;
                    preview[y * width + x] = PreviewColor(values[source], scalar[source]);
                }
            }
            return preview;
        }

        private static Color32 PreviewColor(Color color, bool scalar)
        {
            if (scalar)
            {
                byte value = ToByte(color.r);
                return new Color32(value, value, value, value);
            }
            return new Color32(ToByte(color.r), ToByte(color.g), ToByte(color.b), ToByte(color.a));
        }

        private static byte ToByte(float value)
            => (byte)Mathf.RoundToInt(Mathf.Clamp01(value) * 255);

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
