using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TexturePackEditor;
using UnityEditor;
using UnityEngine;

// Run in an isolated Unity project with Editor/* copied to Assets/Editor.
public static class HeightIntegrationChecks
{
    static int checks;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
    public static void Run()
    {
        try { Verify(); EditorApplication.Exit(0); }
        catch (Exception e) { Debug.LogException(e); File.WriteAllText("height-results.txt", e.ToString()); EditorApplication.Exit(1); }
    }
    public static void Verify()
    {
        const string directory = "Assets/HeightFixtures";
        AssetDatabase.DeleteAsset(directory);
        Directory.CreateDirectory(directory);
        var pixels = new Color32[64 * 64];
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
        {
            double dx = -.4 * Math.Sin((x + .5) / 64 * Math.PI * 2), dy = -.2 * Math.Cos((y + .5) / 64 * Math.PI * 2);
            double length = Math.Sqrt(dx * dx + dy * dy + 1);
            pixels[y * 64 + x] = new Color32(B(-dx / length), B(-dy / length), B(1 / length), 255);
        }
        var normal = SaveTexture(directory + "/normal.png", pixels, true);
        for (int y = 0; y < 64; y++) for (int x = 0; x < 64; x++)
            pixels[y * 64 + x] = new Color32((byte)((x * 3 + y * 7) % 255), (byte)(x * 4), (byte)(y * 4), 255);
        var basemap = SaveTexture(directory + "/base.png", pixels, false);
        var layer = new TerrainLayer { diffuseTexture = basemap, normalMapTexture = normal };
        AssetDatabase.CreateAsset(layer, directory + "/Layer.terrainlayer");
        var recipe = ScriptableObject.CreateInstance<TexturePackRecipe>();
        AssetDatabase.CreateAsset(recipe, directory + "/Recipe.asset");
        var sources = TexturePackSourceSet.FromTerrainLayer(layer, recipe);
        var output = TexturePackOutput.Create(sources, "maskmap");
        output.emptySlotSize = 64;
        recipe.outputs.Add(output);
        recipe.EnsureOutputs();
        foreach (TexturePackHeightMode mode in Enum.GetValues(typeof(TexturePackHeightMode)))
        {
            var node = new TexturePackNode { type = TexturePackNodeType.Height, heightMode = mode,
                sourceRoleId = mode == TexturePackHeightMode.NormalIntegration ? "normal" : "basemap", heightResolution = 128 };
            output.channels[2].nodes.Clear(); output.channels[2].nodes.Add(node);
            sources = TexturePackSourceSet.FromTerrainLayer(layer, recipe);
            var snapshot = output.Clone(true);
            Color32[] preview;
            using (var session = new TexturePackPixelSession(sources, 64, 64, new Rect(0, 0, 1, 1), true))
            {
                session.Prepare(snapshot.channels.SelectMany(c => c.nodes));
                preview = Task.Run(() => TexturePackPreviewWork.Compute(1, snapshot, session)).GetAwaiter().GetResult().outputPixels;
            }
            Check(preview.Select(p => p.b).Distinct().Count() > 10, mode + " produces relief");
            using (var session = new TexturePackPixelSession(sources, 32, 32, new Rect(.25f, .25f, .5f, .5f), true))
            {
                snapshot = output.Clone(true); session.Prepare(snapshot.channels.SelectMany(c => c.nodes));
                var cropped = Task.Run(() => TexturePackPreviewWork.Compute(2, snapshot, session)).GetAwaiter().GetResult().outputPixels;
                Check(Enumerable.Range(0, 32 * 32).All(i => cropped[i].b == preview[(i / 32 + 16) * 64 + i % 32 + 16].b), mode + " crop preserves global shape");
            }
            using (var plan = TexturePackProcessor.PrepareBake(recipe, 0, basemap, "_Height", output.Clone(true), sources))
            {
                Task.Run(() => TexturePackProcessor.ExecuteBake(plan)).GetAwaiter().GetResult();
                var actual = Enumerable.Range(0, 64 * 64).Select(i => TexturePackProcessor.EvaluatePixel(plan.output, plan.session, i, false)).ToArray();
                Check(preview.SequenceEqual(actual), mode + " preview matches generation");
                TexturePackProcessor.CompleteBake(plan, recipe);
                Check(AssetDatabase.GetAssetPath(layer.maskMapTexture) == plan.OutputPath, mode + " assigns generated terrain mask");
            }
            Check(layer.diffuseTexture == basemap && layer.normalMapTexture == normal, mode + " preserves source textures");
            if (mode == TexturePackHeightMode.DeepBump)
            {
                node.heightResolution = 256;
                using var session = new TexturePackPixelSession(sources, 64, 64, new Rect(0, 0, 1, 1), true);
                session.Prepare(new[] { node });
                using var cancellation = new CancellationTokenSource();
                cancellation.CancelAfter(50);
                try { Task.Run(() => TexturePackDeepBump.Infer(node.heightInput, true, cancellation.Token)).GetAwaiter().GetResult(); throw new Exception("DeepBump cancellation ignored"); }
                catch (OperationCanceledException) { checks++; }
                Check(!Directory.EnumerateFiles(Path.Combine(node.heightInput.backend.root, "jobs")).Any(), "Cancelled inference cleans temporary inputs");
            }
        }
        File.WriteAllText("height-results.txt", "PASS: " + checks + " height integration checks");
    }
    static byte B(double n) => (byte)Math.Round((n * .5 + .5) * 255);
    static Texture2D SaveTexture(string path, Color32[] pixels, bool normal)
    {
        var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false, true);
        texture.SetPixels32(pixels); texture.Apply(); File.WriteAllBytes(path, texture.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(texture); AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        importer.textureType = normal ? TextureImporterType.NormalMap : TextureImporterType.Default;
        importer.sRGBTexture = !normal; importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.mipmapEnabled = false; importer.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }
}
