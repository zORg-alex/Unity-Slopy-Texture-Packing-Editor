using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace TexturePackEditor
{
    /// <summary>Whole-image height reconstruction. No Unity objects are accessed by the worker.</summary>
    public static class TexturePackHeight
    {
        internal sealed class Input
        {
            internal Color32[] pixels;
            internal int width, height;
            internal string key;
            internal TexturePackDeepBump.Configuration backend;
        }

        private static readonly object CacheLock = new();
        private static readonly Dictionary<string, float[]> Cache = new();
        private static readonly Queue<string> CacheOrder = new();
        private static long cacheBytes;

        internal static float[] Build(TexturePackNode node, CancellationToken token)
        {
            Input input = node.heightInput ?? throw new InvalidOperationException("Height source was not captured.");
            string key = input.key + ":" + node.heightMode + ":" + node.heightSeamless + ":" + node.heightFlipY +
                ":" + node.heightCoarse.ToString("R", CultureInfo.InvariantCulture) + ":" + node.heightMedium.ToString("R", CultureInfo.InvariantCulture) +
                ":" + node.heightFine.ToString("R", CultureInfo.InvariantCulture) + ":" + node.heightRemoveLighting;
            lock (CacheLock) if (Cache.TryGetValue(key, out var cached)) return cached;
            float[] map = node.heightMode == TexturePackHeightMode.MultiscaleAlbedo
                ? Albedo(input.pixels, input.width, input.height, node, token)
                : Integrate(node.heightMode == TexturePackHeightMode.DeepBump
                    ? TexturePackDeepBump.Infer(input, node.heightSeamless, token) : input.pixels,
                    input.width, input.height, node.heightSeamless, node.heightFlipY, token);
            token.ThrowIfCancellationRequested();
            lock (CacheLock)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;
                long bytes = map.LongLength * sizeof(float);
                while (cacheBytes + bytes > 64L * 1024 * 1024 && CacheOrder.Count > 0)
                {
                    string oldest = CacheOrder.Dequeue();
                    cacheBytes -= Cache[oldest].LongLength * sizeof(float);
                    Cache.Remove(oldest);
                }
                Cache.Add(key, map);
                CacheOrder.Enqueue(key);
                cacheBytes += bytes;
            }
            return map;
        }

        private struct Complex
        {
            public double r, i;
            public Complex(double real, double imaginary = 0) { r = real; i = imaginary; }
            public static Complex operator +(Complex a, Complex b) => new(a.r + b.r, a.i + b.i);
            public static Complex operator -(Complex a, Complex b) => new(a.r - b.r, a.i - b.i);
            public static Complex operator *(Complex a, Complex b) => new(a.r * b.r - a.i * b.i, a.r * b.i + a.i * b.r);
        }

        public static float[] Albedo(Color32[] pixels, int width, int height, TexturePackNode node,
            CancellationToken token = default)
        {
            if (pixels == null || pixels.Length != width * height || width < 1 || height < 1)
                throw new ArgumentException("Albedo dimensions do not match the pixels.");
            var luminance = new float[pixels.Length];
            double sum = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                Color32 c = pixels[i];
                luminance[i] = (.2126f * c.r + .7152f * c.g + .0722f * c.b) / 255f;
                sum += luminance[i];
            }
            int size = Math.Min(width, height);
            var fine = Blur(luminance, width, height, Math.Max(1, size / 256), node.heightSeamless, token);
            var medium = Blur(luminance, width, height, Math.Max(2, size / 64), node.heightSeamless, token);
            var broad = node.heightRemoveLighting
                ? Blur(luminance, width, height, Math.Max(4, size / 16), node.heightSeamless, token) : null;
            float mean = (float)(sum / pixels.Length);
            for (int i = 0; i < luminance.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                luminance[i] = node.heightFine * (luminance[i] - fine[i]) +
                    node.heightMedium * (fine[i] - medium[i]) +
                    node.heightCoarse * (medium[i] - (broad == null ? mean : broad[i]));
            }
            Normalize(luminance);
            return luminance;
        }

        // Three separable box passes approximate a Gaussian, with cost independent of radius.
        private static float[] Blur(float[] source, int width, int height, int radius, bool wrap, CancellationToken token)
        {
            var data = (float[])source.Clone();
            var temporary = new float[data.Length];
            int diameter = radius * 2 + 1;
            for (int pass = 0; pass < 3; pass++)
            {
                for (int y = 0; y < height; y++)
                {
                    token.ThrowIfCancellationRequested();
                    double sum = 0;
                    for (int x = -radius; x <= radius; x++) sum += data[y * width + Boundary(x, width, wrap)];
                    for (int x = 0; x < width; x++)
                    {
                        temporary[y * width + x] = (float)(sum / diameter);
                        sum += data[y * width + Boundary(x + radius + 1, width, wrap)] - data[y * width + Boundary(x - radius, width, wrap)];
                    }
                }
                for (int x = 0; x < width; x++)
                {
                    token.ThrowIfCancellationRequested();
                    double sum = 0;
                    for (int y = -radius; y <= radius; y++) sum += temporary[Boundary(y, height, wrap) * width + x];
                    for (int y = 0; y < height; y++)
                    {
                        data[y * width + x] = (float)(sum / diameter);
                        sum += temporary[Boundary(y + radius + 1, height, wrap) * width + x] - temporary[Boundary(y - radius, height, wrap) * width + x];
                    }
                }
            }
            return data;
        }

        private static int Boundary(int i, int count, bool wrap)
        {
            if (wrap) return Index(i, count, true);
            int reflected = (i % (2 * count) + 2 * count) % (2 * count);
            return reflected < count ? reflected : 2 * count - reflected - 1;
        }

        public static float[] Integrate(Color32[] normals, int width, int height, bool seamless, bool flipY,
            CancellationToken token = default)
        {
            if (width < 2 || height < 2 || (width & (width - 1)) != 0 || (height & (height - 1)) != 0 ||
                normals == null || normals.Length != width * height)
                throw new ArgumentException("Normal integration requires power-of-two dimensions and matching pixels.");
            int w = seamless ? width : width * 2, h = seamless ? height : height * 2;
            var dx = new Complex[w * h];
            var dy = new Complex[w * h];
            for (int y = 0; y < h; y++)
            {
                token.ThrowIfCancellationRequested();
                int sy = y < height ? y : h - y - 1;
                for (int x = 0; x < w; x++)
                {
                    int sx = x < width ? x : w - x - 1;
                    Color32 n = normals[sy * width + sx];
                    double nz = Math.Max(.05, n.b / 127.5 - 1);
                    double nx = n.r == 127 || n.r == 128 ? 0 : n.r / 127.5 - 1;
                    double ny = n.g == 127 || n.g == 128 ? 0 : n.g / 127.5 - 1;
                    // Slopes in UV space; reflection negates the derivative on its reflected axis.
                    dx[y * w + x] = new Complex(-nx / nz * (x < width ? 1 : -1));
                    dy[y * w + x] = new Complex(-ny / nz * (y < height ? 1 : -1) * (flipY ? -1 : 1));
                }
            }
            Transform2D(dx, w, h, false, token);
            Transform2D(dy, w, h, false, token);
            for (int y = 0; y < h; y++)
            {
                token.ThrowIfCancellationRequested();
                double ky = y == h / 2 ? 0 : 2 * Math.PI * (y <= h / 2 ? y : y - h) * height / h;
                for (int x = 0; x < w; x++)
                {
                    double kx = x == w / 2 ? 0 : 2 * Math.PI * (x <= w / 2 ? x : x - w) * width / w;
                    double denominator = kx * kx + ky * ky;
                    int p = y * w + x;
                    dx[p] = denominator < 1e-12 ? default : new Complex(
                        (kx * dx[p].i + ky * dy[p].i) / denominator,
                        -(kx * dx[p].r + ky * dy[p].r) / denominator);
                }
            }
            Transform2D(dx, w, h, true, token);
            var result = new float[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++) result[y * width + x] = (float)dx[y * w + x].r;
            Normalize(result);
            return result;
        }

        internal static void Normalize(float[] values)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (float v in values) { min = Math.Min(min, v); max = Math.Max(max, v); }
            float range = max - min;
            for (int i = 0; i < values.Length; i++) values[i] = range < 1e-6f ? .5f : (values[i] - min) / range;
        }

        private static void Transform2D(Complex[] data, int width, int height, bool inverse, CancellationToken token)
        {
            var line = new Complex[Math.Max(width, height)];
            for (int y = 0; y < height; y++)
            {
                token.ThrowIfCancellationRequested();
                Array.Copy(data, y * width, line, 0, width);
                Transform(line, width, inverse);
                Array.Copy(line, 0, data, y * width, width);
            }
            for (int x = 0; x < width; x++)
            {
                token.ThrowIfCancellationRequested();
                for (int y = 0; y < height; y++) line[y] = data[y * width + x];
                Transform(line, height, inverse);
                for (int y = 0; y < height; y++) data[y * width + x] = line[y];
            }
        }

        private static void Transform(Complex[] data, int count, bool inverse)
        {
            for (int i = 1, j = 0; i < count; i++)
            {
                int bit = count >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (data[i], data[j]) = (data[j], data[i]);
            }
            for (int length = 2; length <= count; length <<= 1)
            {
                double angle = (inverse ? 2 : -2) * Math.PI / length;
                var step = new Complex(Math.Cos(angle), Math.Sin(angle));
                for (int start = 0; start < count; start += length)
                {
                    var factor = new Complex(1);
                    for (int j = 0; j < length / 2; j++)
                    {
                        Complex even = data[start + j], odd = data[start + j + length / 2] * factor;
                        data[start + j] = even + odd;
                        data[start + j + length / 2] = even - odd;
                        factor *= step;
                    }
                }
            }
            if (inverse) for (int i = 0; i < count; i++) { data[i].r /= count; data[i].i /= count; }
        }

        public static float Sample(float[] values, int width, int height, float u, float v, bool wrap)
        {
            float px = u * width - .5f, py = v * height - .5f;
            int x = (int)Math.Floor(px), y = (int)Math.Floor(py);
            float tx = px - x, ty = py - y;
            int x0 = Index(x, width, wrap), x1 = Index(x + 1, width, wrap);
            int y0 = Index(y, height, wrap), y1 = Index(y + 1, height, wrap);
            return (values[y0 * width + x0] * (1 - tx) + values[y0 * width + x1] * tx) * (1 - ty) +
                (values[y1 * width + x0] * (1 - tx) + values[y1 * width + x1] * tx) * ty;
        }

        private static int Index(int i, int count, bool wrap) => wrap ? (i % count + count) % count : Math.Max(0, Math.Min(count - 1, i));
    }
}
