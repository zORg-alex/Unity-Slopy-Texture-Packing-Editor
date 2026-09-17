using System; using System.IO; using System.Reflection; using TexturePackEditor; using UnityEditor; using UnityEngine;
public static class RecipeUndoChecks {
 public static void Run(){ TexturePackEditorWindow window=null; try {
 var recipe=ScriptableObject.CreateInstance<TexturePackRecipe>(); recipe.EnsureOutputs(); var node=new TexturePackNode{type=TexturePackNodeType.Height,heightCenter=.5f}; recipe.outputs[0].channels[0].nodes.Add(node);
 AssetDatabase.CreateAsset(recipe,AssetDatabase.GenerateUniqueAssetPath("Assets/UndoRecipe.asset"));
 window=ScriptableObject.CreateInstance<TexturePackEditorWindow>(); var flags=BindingFlags.Instance|BindingFlags.NonPublic; typeof(TexturePackEditorWindow).GetField("recipe",flags).SetValue(window,recipe);
 Undo.IncrementCurrentGroup(); typeof(TexturePackEditorWindow).GetMethod("RecordRecipeUndo",flags).Invoke(window,null); node.heightCenter=.8f;node.blendAmount=.3f;Undo.FlushUndoRecordObjects();Undo.PerformUndo();
 if(recipe.outputs[0].channels[0].nodes[0].heightCenter!=.5f||recipe.outputs[0].channels[0].nodes[0].blendAmount!=1)throw new Exception("Undo failed");
 Undo.PerformRedo(); if(recipe.outputs[0].channels[0].nodes[0].heightCenter!=.8f)throw new Exception("Redo failed");
 if(!(bool)typeof(TexturePackEditorWindow).GetField("_previewDirty",flags).GetValue(window))throw new Exception("Preview not invalidated");
 UnityEngine.Object.DestroyImmediate(window); File.WriteAllText("undo-results.txt","PASS: saved recipe center/blend Undo, Redo and preview invalidation");EditorApplication.Exit(0);
 }catch(Exception e){if(window!=null)UnityEngine.Object.DestroyImmediate(window);File.WriteAllText("undo-results.txt",e.ToString());EditorApplication.Exit(1);}}
}
