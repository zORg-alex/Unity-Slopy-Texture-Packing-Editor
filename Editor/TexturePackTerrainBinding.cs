using System;
using UnityEditor;
using UnityEngine;

namespace TexturePackEditor
{
    // These IDs are the built-in semantic roles, not their editable display names.
    public static class TexturePackTerrainBinding
    {
        public static readonly string[] Roles = { "basemap", "normal", "maskmap" };

        public static bool Supports(string role)
            => Array.Exists(Roles, candidate => string.Equals(candidate, role, StringComparison.OrdinalIgnoreCase));

        public static Texture2D GetTexture(TerrainLayer layer, string role)
        {
            if (layer == null) return null;
            return role?.ToLowerInvariant() switch
            {
                "basemap" => layer.diffuseTexture,
                "normal" => layer.normalMapTexture,
                "maskmap" => layer.maskMapTexture,
                _ => null
            };
        }

        public static void Apply(TerrainLayer layer, string role, Texture2D texture, Texture2D expected)
        {
            if (layer == null || !Supports(role)) return;
            if (GetTexture(layer, role) != expected)
                throw new InvalidOperationException("The terrain layer's " + role +
                    " texture changed during generation. The generated file was saved, but the layer was not changed.");
            Undo.RecordObject(layer, "Apply generated terrain texture");
            switch (role.ToLowerInvariant())
            {
                case "basemap": layer.diffuseTexture = texture; break;
                case "normal": layer.normalMapTexture = texture; break;
                case "maskmap": layer.maskMapTexture = texture; break;
            }
            EditorUtility.SetDirty(layer);
            AssetDatabase.SaveAssetIfDirty(layer);
        }
    }
}
