using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TexturePackEditor;
using UnityEditor;
using UnityEngine;

public static class PreviewSpinnerChecks
{
    public static void Verify()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var window = ScriptableObject.CreateInstance<TexturePackEditorWindow>();
        try
        {
            var type = typeof(TexturePackEditorWindow);
            var recipe = (TexturePackRecipe)type.GetField("recipe", flags).GetValue(window);
            recipe.EnsureOutputs();
            var node = new TexturePackNode { type = TexturePackNodeType.Constant };
            recipe.outputs[0].channels[0].nodes.Add(node);
            type.GetMethod("MarkPendingPreviews", flags).Invoke(window, new object[] { 1 });
            var pending = (Dictionary<string, double>)type.GetField("_pendingPreviewNodes", flags).GetValue(window);
            if (!pending.ContainsKey(node.id)) throw new Exception("Node was not tracked");
            pending[node.id] = 100;
            var busy = type.GetMethod("NodePreviewBusy", flags);
            if ((bool)busy.Invoke(window, new object[] { node.id, 100.19 })) throw new Exception("Spinner appears too soon");
            if (!(bool)busy.Invoke(window, new object[] { node.id, 100.21 })) throw new Exception("Spinner appears too late");
            type.GetMethod("Changed", flags).Invoke(window, new object[] { 1 });
            if (pending[node.id] != 100) throw new Exception("Repeated slider edits reset the pending timer");
            type.GetMethod("ApplyPreviewUpdate", flags).Invoke(window, new object[] {
                new TexturePackPreviewUpdate { nodeId = node.id, width = 1, height = 1, pixels = new[] { new Color32(128, 128, 128, 255) } } });
            if ((bool)busy.Invoke(window, new object[] { node.id, 200d })) throw new Exception("Completed preview still spins");
            for (int frame = 0; frame < 12; frame++)
                if (EditorGUIUtility.IconContent("WaitSpin" + frame.ToString("00")).image == null)
                    throw new Exception("Missing editor spinner frame " + frame);
            File.WriteAllText("spinner-results.txt", "PASS: 0.2 second threshold, per-node completion, repeated-edit timer retention, all 12 editor spinner icons");
        }
        finally { UnityEngine.Object.DestroyImmediate(window); }
    }
}
