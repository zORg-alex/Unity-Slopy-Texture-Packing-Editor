using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TexturePackEditor;
using UnityEditor;
using UnityEngine;

public static class HeightSliderChecks
{
    static TexturePackEditorWindow window;
    static TexturePackRecipe recipe;
    static TexturePackSourceSet sources;
    static Texture2D large;
    static int iteration;
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run()
    {
        try
        {
            HeightIntegrationChecks.Verify();
            recipe = AssetDatabase.LoadAssetAtPath<TexturePackRecipe>("Assets/HeightFixtures/Recipe.asset");
            sources = TexturePackSourceSet.FromTerrainLayer(AssetDatabase.LoadAssetAtPath<TerrainLayer>("Assets/HeightFixtures/Layer.terrainlayer"), recipe);
            window = ScriptableObject.CreateInstance<TexturePackEditorWindow>();
            typeof(TexturePackEditorWindow).GetField("showSelectedNode", Flags).SetValue(window, true);
            typeof(TexturePackEditorWindow).GetField("_lastSelectedId", Flags).SetValue(window, recipe.outputs[0].channels[2].nodes[0].id);
            EditorApplication.update += Tick;
        }
        catch (Exception e) { Finish(e); }
    }
    static void Tick()
    {
        try
        {
            var output = recipe.outputs[0].Clone(true);
            var node = output.channels[2].nodes[0];
            node.heightCenter = .1f + .8f * (iteration % 21) / 20f;
            node.heightStrength = .5f + (iteration % 7) / 7f;
            using var session = new TexturePackPixelSession(sources, 64, 64, new Rect(0, 0, 1, 1), true);
            session.Prepare(output.channels.SelectMany(c => c.nodes));
            if (node.heightInput.cachedMap == null || node.heightInput.pixels != null || node.heightInput.backend != null)
                throw new Exception("Slider change recaptures or reinfers its source");
            var result = Task.Run(() => TexturePackPreviewWork.Compute(iteration, output, session)).GetAwaiter().GetResult();
            var update = new TexturePackPreviewUpdate { nodeId = node.id, width = 64, height = 64, pixels = result.nodePixels[node.id] };
            typeof(TexturePackEditorWindow).GetMethod("ApplyPreviewUpdate", Flags).Invoke(window, new object[] { update });
            var current = (Texture2D)typeof(TexturePackEditorWindow).GetField("_largePreview", Flags).GetValue(window);
            if (iteration > 0 && !ReferenceEquals(current, large)) throw new Exception("Slider reallocates native preview texture");
            large = current;
            if (!large.isReadable) throw new Exception("Reusable preview lost CPU storage");
            if (++iteration == 200) Finish(null);
        }
        catch (Exception e) { Finish(e); }
    }
    static void Finish(Exception error)
    {
        EditorApplication.update -= Tick;
        if (window != null) UnityEngine.Object.DestroyImmediate(window);
        File.WriteAllText("height-slider-results.txt", error == null
            ? "PASS: 200 Center/Strength updates across editor frames; zero height recaptures or inference restarts; stable preview texture identity"
            : error.ToString());
        if (error != null) Debug.LogException(error);
        EditorApplication.Exit(error == null ? 0 : 1);
    }
}
