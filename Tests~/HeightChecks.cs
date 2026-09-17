using System;
using System.Threading;
using TexturePackEditor;
using UnityEngine;

// Compile alongside Editor/*.cs with Unity references; no running editor is required.
public static class HeightChecks
{
    public static void Main()
    {
        const int w = 64, h = 32;
        var normals = new Color32[w * h];
        var expected = new float[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            double u = (x + .5) / w * 2 * Math.PI, v = (y + .5) / h * 2 * Math.PI;
            double dx = -.3 * Math.Sin(u), dy = -.15 * Math.Sin(v);
            double length = Math.Sqrt(dx * dx + dy * dy + 1);
            normals[y * w + x] = new Color32(Byte(-dx / length), Byte(-dy / length), Byte(1 / length), 255);
            expected[y * w + x] = (float)(.3 * Math.Cos(u) + .15 * Math.Cos(v));
        }
        TexturePackHeight.Normalize(expected);
        var result = TexturePackHeight.Integrate(normals, w, h, true, false);
        double error = 0;
        for (int i = 0; i < result.Length; i++) error += Math.Abs(result[i] - expected[i]);
        if (error / result.Length > .01) throw new Exception("Analytic normal integration: " + error / result.Length);
        for (int i = 0; i < normals.Length; i++) normals[i] = new Color32(128, 128, 255, 255);
        foreach (bool seamless in new[] { true, false })
        {
            result = TexturePackHeight.Integrate(normals, w, h, seamless, false);
            foreach (float value in result) if (Math.Abs(value - .5f) > .001f) throw new Exception("Flat normal must stay flat");
        }
        var cancelled = new CancellationToken(true);
        try { TexturePackHeight.Integrate(normals, w, h, true, false, cancelled); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        if (Math.Abs(TexturePackHeight.Sample(new[] { 0f, 1f, 0f, 1f }, 2, 2, .5f, .5f, true) - .5f) > 1e-6)
            throw new Exception("Bilinear sampling");
        var node = new TexturePackNode { heightMode = TexturePackHeightMode.MultiscaleAlbedo };
        foreach (float value in TexturePackHeight.Albedo(normals, w, h, node))
            if (Math.Abs(value - .5f) > .001) throw new Exception("Flat albedo must stay flat");
        var shifted = new Color32[normals.Length];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            normals[y * w + x] = new Color32((byte)((x * 31 + y * 13) % 255), (byte)((x * 5 + y * 17) % 255), 90, 255);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) shifted[y * w + x] = normals[y * w + (x + 7) % w];
        var original = TexturePackHeight.Albedo(normals, w, h, node);
        var translated = TexturePackHeight.Albedo(shifted, w, h, node);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            if (Math.Abs(translated[y * w + x] - original[y * w + (x + 7) % w]) > .0001)
                throw new Exception("Seamless albedo translation invariance");
        node.heightCoarse = node.heightMedium = node.heightFine = 0;
        foreach (float value in TexturePackHeight.Albedo(normals, w, h, node))
            if (value != .5f) throw new Exception("Zero detail bands must stay flat");
        Console.WriteLine("PASS: analytic reconstruction, flat maps, cancellation and bilinear sampling");
        Console.WriteLine("PASS: flat albedo, seamless translation and disabled detail bands");
    }
    static byte Byte(double value) => (byte)Math.Max(0, Math.Min(255, Math.Round((value * .5 + .5) * 255)));
}
