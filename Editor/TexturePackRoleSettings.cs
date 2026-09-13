using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace TexturePackEditor
{
    public enum TexturePackRuleMatchMode { LiteralTerms, Regex }
    public enum TexturePackRuleOverrideMode { Add, Override }

    [Serializable]
    public sealed class TexturePackRoleDefinition
    {
        public string id = Guid.NewGuid().ToString("N");
        public string name = "Role";
        public TexturePackRuleMatchMode matchMode;
        public string expression;
    }

    [Serializable]
    public sealed class TexturePackRoleOverride
    {
        public string roleId;
        public TexturePackRuleOverrideMode mode;
        public TexturePackRuleMatchMode matchMode;
        public string expression;
    }

    [FilePath("ProjectSettings/TexturePackEditorSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    public sealed class TexturePackProjectSettings : ScriptableSingleton<TexturePackProjectSettings>
    {
        [SerializeField] private List<TexturePackRoleDefinition> roles = new();

        public IReadOnlyList<TexturePackRoleDefinition> Roles
        {
            get { EnsureDefaults(); return roles; }
        }

        public List<TexturePackRoleDefinition> EditableRoles
        {
            get { EnsureDefaults(); return roles; }
        }

        public void EnsureDefaults()
        {
            roles ??= new List<TexturePackRoleDefinition>();
            if (roles.Count != 0) return;
            roles.Add(Default("basemap", "Basemap", "albedo|_a|basemap|basecolor|base|color"));
            roles.Add(Default("maskmap", "MaskMap", "maskmap|mask|_o|occlusion"));
            roles.Add(Default("normal", "Normal", "normal|_n"));
            roles.Add(Default("specular", "Specular", "specular|spec|_sg"));
        }

        public void ResetDefaults()
        {
            roles = new List<TexturePackRoleDefinition>();
            EnsureDefaults();
            Save(true);
        }

        public void SaveSettings() => Save(true);

        public TexturePackRoleDefinition Find(string roleId)
        {
            EnsureDefaults();
            return roles.FirstOrDefault(role => string.Equals(role.id, roleId, StringComparison.OrdinalIgnoreCase));
        }

        public string DisplayName(string roleId)
        {
            TexturePackRoleDefinition role = Find(roleId);
            return role == null ? string.IsNullOrEmpty(roleId) ? "Missing" : roleId : role.name;
        }

        private static TexturePackRoleDefinition Default(string id, string name, string expression)
            => new() { id = id, name = name, expression = expression };
    }

    public static class TexturePackRoleMatcher
    {
        private readonly struct MatchResult
        {
            public readonly string RoleId;
            public readonly int Length;

            public MatchResult(string roleId, int length)
            {
                RoleId = roleId;
                Length = length;
            }
        }

        public static string Match(string rawRole, TexturePackRecipe recipe, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(rawRole)) return null;
            var matches = new List<MatchResult>();
            foreach (TexturePackRoleDefinition role in TexturePackProjectSettings.instance.Roles)
            {
                int length = MatchRole(rawRole, role, recipe, out string roleError);
                if (!string.IsNullOrEmpty(roleError))
                {
                    error = string.IsNullOrEmpty(error) ? roleError : error + "\n" + roleError;
                    continue;
                }
                if (length > 0) matches.Add(new MatchResult(role.id, length));
            }
            if (matches.Count == 0) return null;
            int longest = matches.Max(match => match.Length);
            string[] winners = matches.Where(match => match.Length == longest).Select(match => match.RoleId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (winners.Length == 1) return winners[0];
            error = "Ambiguous role: " + string.Join(", ", winners.Select(TexturePackProjectSettings.instance.DisplayName));
            return null;
        }

        public static string ResolveLegacy(string legacyRole, TexturePackRecipe recipe)
        {
            if (string.IsNullOrWhiteSpace(legacyRole)) return null;
            foreach (TexturePackRoleDefinition role in TexturePackProjectSettings.instance.Roles)
                if (string.Equals(role.id, legacyRole, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(role.name, legacyRole, StringComparison.OrdinalIgnoreCase)) return role.id;
            return Match(legacyRole, recipe, out _);
        }

        public static bool Validate(TexturePackRuleMatchMode mode, string expression, out string error)
        {
            error = null;
            if (mode != TexturePackRuleMatchMode.Regex || string.IsNullOrWhiteSpace(expression)) return true;
            try { _ = new Regex(expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
            catch (ArgumentException exception) { error = exception.Message; return false; }
            return true;
        }

        private static int MatchRole(string rawRole, TexturePackRoleDefinition role, TexturePackRecipe recipe,
            out string error)
        {
            error = null;
            TexturePackRoleOverride ruleOverride = recipe?.RoleOverride(role.id);
            int best = 0;
            if (ruleOverride == null || ruleOverride.mode == TexturePackRuleOverrideMode.Add)
                best = MatchExpression(rawRole, role.matchMode, role.expression, role.name, out error);
            if (ruleOverride == null) return best;
            int overrideMatch = MatchExpression(rawRole, ruleOverride.matchMode, ruleOverride.expression,
                role.name + " override", out string overrideError);
            if (!string.IsNullOrEmpty(overrideError)) error = overrideError;
            return overrideMatch > 0 ? 100000 + overrideMatch : best;
        }

        private static int MatchExpression(string rawRole, TexturePackRuleMatchMode mode, string expression,
            string label, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(expression)) return 0;
            string candidate = "_" + rawRole;
            if (mode == TexturePackRuleMatchMode.LiteralTerms)
            {
                int best = 0;
                foreach (string value in expression.Split('|'))
                {
                    string term = value.Trim();
                    if (term.Length > 0 && candidate.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        best = Mathf.Max(best, term.Length);
                }
                return best;
            }
            try
            {
                MatchCollection matches = Regex.Matches(candidate, expression,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                return matches.Count == 0 ? 0 : matches.Cast<Match>().Max(match => match.Length);
            }
            catch (ArgumentException exception)
            {
                error = label + ": " + exception.Message;
                return 0;
            }
        }
    }

    public static class TexturePackSessionBindings
    {
        private static readonly Dictionary<string, Texture2D> Bindings = new(StringComparer.OrdinalIgnoreCase);

        public static Texture2D Get(string familyKey, string roleId)
            => Bindings.TryGetValue(Key(familyKey, roleId), out Texture2D texture) ? texture : null;

        public static void Set(string familyKey, string roleId, Texture2D texture)
        {
            string key = Key(familyKey, roleId);
            if (texture == null) Bindings.Remove(key);
            else Bindings[key] = texture;
        }

        private static string Key(string familyKey, string roleId) => familyKey + "|" + roleId;
    }

    public sealed class TexturePackRoleSettingsWindow : EditorWindow
    {
        private Texture2D _anchor;
        private TexturePackRecipe _recipe;
        private Action _changed;
        private Vector2 _scroll;

        public static void Open(Texture2D anchor, TexturePackRecipe recipe, Action changed)
        {
            var window = CreateInstance<TexturePackRoleSettingsWindow>();
            window.titleContent = new GUIContent("Texture Roles");
            window.minSize = new Vector2(620, 420);
            window._anchor = anchor;
            window._recipe = recipe;
            window._changed = changed;
            window.ShowUtility();
        }

        private void OnGUI()
        {
            TexturePackProjectSettings settings = TexturePackProjectSettings.instance;
            settings.EnsureDefaults();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            DrawProjectRoles(settings);
            EditorGUILayout.Space(10);
            DrawRecipeOverrides(settings);
            EditorGUILayout.Space(10);
            DrawDetectionPreview(settings);
            EditorGUILayout.EndScrollView();
        }

        private void DrawProjectRoles(TexturePackProjectSettings settings)
        {
            EditorGUILayout.LabelField("Project Roles", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            List<TexturePackRoleDefinition> roles = settings.EditableRoles;
            for (int index = 0; index < roles.Count; index++)
            {
                TexturePackRoleDefinition role = roles[index];
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        role.name = EditorGUILayout.TextField(role.name, GUILayout.MinWidth(110));
                        role.matchMode = (TexturePackRuleMatchMode)EditorGUILayout.EnumPopup(role.matchMode,
                            GUILayout.Width(92));
                        role.expression = EditorGUILayout.TextField(role.expression, GUILayout.MinWidth(220));
                        using (new EditorGUI.DisabledScope(index == 0))
                            if (GUILayout.Button("▲", GUILayout.Width(24))) Move(roles, index, index - 1);
                        using (new EditorGUI.DisabledScope(index == roles.Count - 1))
                            if (GUILayout.Button("▼", GUILayout.Width(24))) Move(roles, index, index + 1);
                        if (GUILayout.Button("×", GUILayout.Width(24)))
                        {
                            roles.RemoveAt(index--);
                            continue;
                        }
                    }
                    if (!TexturePackRoleMatcher.Validate(role.matchMode, role.expression, out string error))
                        EditorGUILayout.HelpBox(error, MessageType.Error);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Role", GUILayout.Width(90))) roles.Add(new TexturePackRoleDefinition());
                if (GUILayout.Button("Reset Defaults", GUILayout.Width(110))) settings.ResetDefaults();
            }
            if (EditorGUI.EndChangeCheck())
            {
                settings.SaveSettings();
                Changed();
            }
        }

        private void DrawRecipeOverrides(TexturePackProjectSettings settings)
        {
            EditorGUILayout.LabelField("Recipe Overrides", EditorStyles.boldLabel);
            if (_recipe == null)
            {
                EditorGUILayout.HelpBox("Save or select a recipe to add recipe-specific rules.", MessageType.Info);
                return;
            }
            _recipe.EnsureOutputs();
            EditorGUI.BeginChangeCheck();
            foreach (TexturePackRoleDefinition role in settings.Roles)
            {
                TexturePackRoleOverride rule = _recipe.RoleOverride(role.id);
                using (new EditorGUILayout.HorizontalScope())
                {
                    bool enabled = EditorGUILayout.ToggleLeft(role.name, rule != null, GUILayout.Width(120));
                    if (enabled && rule == null)
                    {
                        rule = new TexturePackRoleOverride { roleId = role.id };
                        _recipe.roleOverrides.Add(rule);
                    }
                    else if (!enabled && rule != null)
                    {
                        _recipe.roleOverrides.Remove(rule);
                        rule = null;
                    }
                    using (new EditorGUI.DisabledScope(rule == null))
                    {
                        if (GUILayout.Button(rule?.mode == TexturePackRuleOverrideMode.Override ? "Override" : "Add",
                                GUILayout.Width(66)) && rule != null)
                            rule.mode = rule.mode == TexturePackRuleOverrideMode.Add
                                ? TexturePackRuleOverrideMode.Override : TexturePackRuleOverrideMode.Add;
                        TexturePackRuleMatchMode mode = rule?.matchMode ?? TexturePackRuleMatchMode.LiteralTerms;
                        mode = (TexturePackRuleMatchMode)EditorGUILayout.EnumPopup(mode, GUILayout.Width(92));
                        string expression = EditorGUILayout.TextField(rule?.expression ?? string.Empty);
                        if (rule != null) { rule.matchMode = mode; rule.expression = expression; }
                    }
                }
                if (rule != null && !TexturePackRoleMatcher.Validate(rule.matchMode, rule.expression, out string error))
                    EditorGUILayout.HelpBox(error, MessageType.Error);
            }
            if (EditorGUI.EndChangeCheck())
            {
                EditorUtility.SetDirty(_recipe);
                Changed();
            }
        }

        private void DrawDetectionPreview(TexturePackProjectSettings settings)
        {
            EditorGUILayout.LabelField("Detection Preview", EditorStyles.boldLabel);
            if (_anchor == null)
            {
                EditorGUILayout.HelpBox("Choose an anchor texture in the main window.", MessageType.Info);
                return;
            }
            TexturePackSourceSet sourceSet = TexturePackSourceSet.Detect(_anchor, _recipe);
            foreach (TexturePackRoleDefinition role in settings.Roles)
            {
                if (sourceSet.Conflicts.TryGetValue(role.id, out List<Texture2D> conflicts))
                {
                    Texture2D selected = TexturePackSessionBindings.Get(sourceSet.FamilyKey, role.id);
                    Texture2D next = (Texture2D)EditorGUILayout.ObjectField(role.name + " (conflict)", selected,
                        typeof(Texture2D), false);
                    if (next != selected && (next == null || conflicts.Contains(next)))
                    {
                        TexturePackSessionBindings.Set(sourceSet.FamilyKey, role.id, next);
                        Changed();
                    }
                    EditorGUILayout.LabelField("Matches: " + string.Join(", ", conflicts.Select(item => item.name)),
                        EditorStyles.miniLabel);
                }
                else if (sourceSet.Detected.TryGetValue(role.id, out Texture2D texture))
                    EditorGUILayout.ObjectField(role.name, texture, typeof(Texture2D), false);
            }
            if (sourceSet.Unmatched.Count > 0)
                EditorGUILayout.HelpBox("Unmatched: " + string.Join(", ", sourceSet.Unmatched.Select(item => item.name)),
                    MessageType.Info);
            foreach (string error in sourceSet.Errors) EditorGUILayout.HelpBox(error, MessageType.Warning);
        }

        private void Changed()
        {
            _changed?.Invoke();
            Repaint();
        }

        private static void Move<T>(List<T> list, int from, int to)
        {
            T value = list[from];
            list.RemoveAt(from);
            list.Insert(to, value);
        }
    }
}
