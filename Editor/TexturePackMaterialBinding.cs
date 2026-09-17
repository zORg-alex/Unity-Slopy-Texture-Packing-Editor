using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace TexturePackEditor
{
    [Serializable]
    public sealed class TexturePackMaterialSlot
    {
        public string roleId;
        // No entry means automatic; an empty property explicitly disables this role.
        public string propertyName;
    }

    public static class TexturePackMaterialBinding
    {
        public static string[] TextureProperties(Material material)
        {
            if (material == null || material.shader == null) return Array.Empty<string>();
            Shader shader = material.shader;
            var properties = new List<string>();
            for (int index = 0; index < shader.GetPropertyCount(); index++)
                if (shader.GetPropertyType(index) == ShaderPropertyType.Texture &&
                    shader.GetPropertyTextureDimension(index) == TextureDimension.Tex2D)
                    properties.Add(shader.GetPropertyName(index));
            return properties.ToArray();
        }

        public static string RoleForProperty(string property, TexturePackRecipe recipe)
        {
            // Detail maps are separate inputs, not aliases for the primary material maps.
            if (property.StartsWith("_Detail", StringComparison.OrdinalIgnoreCase)) return null;
            string role = property.ToLowerInvariant() switch
            {
                "_maintex" or "_basemap" or "_basecolormap" or "_albedomap" or "_albedo" or "_basecolor" => "basemap",
                "_bumpmap" or "_normalmap" or "_normal" => "normal",
                "_maskmap" or "_occlusionmap" or "_occlusion" => "maskmap",
                "_specglossmap" or "_specularcolormap" or "_specularmap" => "specular",
                _ => null
            };
            if (role != null && TexturePackProjectSettings.instance.Find(role) != null) return role;
            return TexturePackRoleMatcher.Match(property.TrimStart('_'), recipe, out _);
        }

        public static void Apply(Material material, Shader shader, string property, Texture2D texture,
            Texture2D expected)
        {
            if (material == null)
                throw new InvalidOperationException("The target material was removed during generation. The generated file was saved.");
            if (material.shader != shader || !TextureProperties(material).Contains(property) ||
                material.GetTexture(property) != expected)
                throw new InvalidOperationException("The material's shader or " + property +
                    " texture changed during generation. The generated file was saved, but the material was not changed.");
            Undo.RecordObject(material, "Apply generated material texture");
            material.SetTexture(property, texture);
            if (expected == null || string.IsNullOrEmpty(AssetDatabase.GetAssetPath(expected)))
            {
                string keywordName = property switch
                {
                    "_MaskMap" => "_MASKMAP",
                    "_BumpMap" or "_NormalMap" => "_NORMALMAP",
                    "_OcclusionMap" => "_OCCLUSIONMAP",
                    "_SpecGlossMap" => "_SPECGLOSSMAP",
                    "_MetallicGlossMap" => "_METALLICGLOSSMAP",
                    _ => null
                };
                if (keywordName != null)
                {
                    var keyword = shader.keywordSpace.FindKeyword(keywordName);
                    if (keyword.isValid) material.EnableKeyword(keyword);
                }
            }
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
        }
    }
}
