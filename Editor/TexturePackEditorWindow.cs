using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace TexturePackEditor
{
    public sealed class TexturePackEditorWindow : EditorWindow
    {
        private const string SuffixKey = "TexturePackEditor.SafeOutputSuffix";
        private const string DragKey = "TexturePackEditor.DragPayload";
        private static readonly string[] ChannelNames = { "R", "G", "B", "A" };
        private static List<TexturePackNode> _clipboard = new();

        [SerializeField] private Texture2D anchor;
        [SerializeField] private TexturePackRecipe recipe;
        private TexturePackRecipe _transientRecipe;
        private Texture2D _manualCandidate;
        private TexturePackSourceSet _sourceSet;
        private readonly Dictionary<string, int> _samplerMasks = new();
        private readonly HashSet<string> _selection = new();
        private readonly Dictionary<string, Texture2D> _nodePreviews = new();
        private readonly Vector2[] _channelScroll = new Vector2[4];
        private Texture2D _outputPreview;
        private Vector2 _sourcesScroll;
        private string _lastSelectedId;
        private int _lastSelectedChannel = -1;
        private int _activeChannel;
        private bool _previewDirty = true;
        private string _previewError;

        private sealed class DragPayload
        {
            public TexturePackNode Node;
            public List<string> NodeIds;
        }

        [MenuItem("Tools/Texture Pack Editor")]
        public static void Open()
        {
            var window = GetWindow<TexturePackEditorWindow>();
            window.titleContent = new GUIContent("Texture Pack Editor");
            window.minSize = new Vector2(940, 560);
            window.Show();
        }

        private void OnEnable()
        {
            Selection.selectionChanged += OnProjectSelectionChanged;
            if (anchor == null && Selection.activeObject is Texture2D selected) anchor = selected;
            EnsureRecipe();
            RefreshSourceSet(false);
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnProjectSelectionChanged;
            DestroyPreviews();
            if (recipe != null && recipe != _transientRecipe) AssetDatabase.SaveAssetIfDirty(recipe);
            if (_transientRecipe != null) DestroyImmediate(_transientRecipe);
        }

        private void OnProjectSelectionChanged()
        {
            if (Selection.activeObject is not Texture2D selected || selected == anchor) return;
            anchor = selected;
            RefreshSourceSet(recipe == _transientRecipe);
            Repaint();
        }

        private void OnGUI()
        {
            HandleKeyboard();
            DrawHeader();
            if (anchor == null)
            {
                EditorGUILayout.HelpBox("Select or drag a texture to begin. Adjacent textures sharing its filename prefix are detected automatically.", MessageType.Info);
                return;
            }

            EnsureRecipe();
            recipe.EnsureChannels();
            // IMGUI must see the same preview controls during Layout and Repaint.
            if (_previewDirty && Event.current.type == EventType.Layout) RebuildPreviews();

            EditorGUILayout.Space(3);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(245))) DrawSources();
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawActionToolbar();
                    DrawOutputStacks();
                }
            }
        }

        private void DrawHeader()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    var nextAnchor = (Texture2D)EditorGUILayout.ObjectField("Anchor", anchor, typeof(Texture2D), false);
                    if (EditorGUI.EndChangeCheck())
                    {
                        anchor = nextAnchor;
                        RefreshSourceSet(recipe == _transientRecipe);
                    }

                    EditorGUI.BeginChangeCheck();
                    var nextRecipe = (TexturePackRecipe)EditorGUILayout.ObjectField(recipe == _transientRecipe ? null : recipe,
                        typeof(TexturePackRecipe), false, GUILayout.Width(210));
                    if (EditorGUI.EndChangeCheck() && nextRecipe != null)
                    {
                        recipe = nextRecipe;
                        recipe.EnsureChannels();
                        Changed();
                    }
                    string createLabel = recipe == _transientRecipe ? "Save Recipe…" : "Duplicate…";
                    if (GUILayout.Button(createLabel, GUILayout.Width(90))) CreateRecipe();
                    using (new EditorGUI.DisabledScope(recipe == null || recipe == _transientRecipe))
                        if (GUILayout.Button("Save", GUILayout.Width(50))) AssetDatabase.SaveAssetIfDirty(recipe);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    string suffix = EditorPrefs.GetString(SuffixKey, "_Wet");
                    EditorGUI.BeginChangeCheck();
                    suffix = EditorGUILayout.TextField("Safe output suffix", suffix, GUILayout.Width(300));
                    if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(SuffixKey, suffix);

                    string[] roles = _sourceSet?.SortedRoles() ?? Array.Empty<string>();
                    int roleIndex = Mathf.Max(0, Array.IndexOf(roles, recipe?.outputBaseRole));
                    using (new EditorGUI.DisabledScope(roles.Length == 0))
                    {
                        EditorGUI.BeginChangeCheck();
                        roleIndex = EditorGUILayout.Popup("Output base", roleIndex, roles, GUILayout.Width(270));
                        if (EditorGUI.EndChangeCheck() && roles.Length > 0)
                        {
                            recipe.outputBaseRole = roles[roleIndex];
                            Changed();
                        }
                    }

                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(recipe == null || recipe == _transientRecipe))
                    {
                        if (GUILayout.Button("Generate Safe TGA", GUILayout.Width(145))) Generate();
                    }
                }
            }
        }

        private void DrawSources()
        {
            EditorGUILayout.LabelField("Sampler Sources", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Toggle sampler components, then drag the sampler card into an output stack.", MessageType.None);
            _sourcesScroll = EditorGUILayout.BeginScrollView(_sourcesScroll);
            if (_sourceSet != null)
                foreach (var pair in _sourceSet.Detected)
                    DrawSampler(pair.Value, TexturePackSourceKind.DetectedRole, pair.Key, "Detected · " + pair.Key);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Manual Sources", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _manualCandidate = (Texture2D)EditorGUILayout.ObjectField(_manualCandidate, typeof(Texture2D), false);
                using (new EditorGUI.DisabledScope(_manualCandidate == null))
                {
                    if (GUILayout.Button("+", GUILayout.Width(28)))
                    {
                        if (!recipe.manualSources.Contains(_manualCandidate)) recipe.manualSources.Add(_manualCandidate);
                        _manualCandidate = null;
                        Changed();
                    }
                }
            }

            for (int i = 0; i < recipe.manualSources.Count; i++)
            {
                Texture2D texture = recipe.manualSources[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUILayout.VerticalScope())
                        DrawSampler(texture, TexturePackSourceKind.ManualTexture, null,
                            texture == null ? "Missing" : texture.name);
                    if (GUILayout.Button("×", GUILayout.Width(22)))
                    {
                        recipe.manualSources.RemoveAt(i--);
                        Changed();
                    }
                }
            }
            EditorGUILayout.EndScrollView();

            if (_outputPreview != null)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Packed RGBA Preview", EditorStyles.boldLabel);
                Rect preview = GUILayoutUtility.GetAspectRect(1, GUILayout.MaxHeight(205));
                EditorGUI.DrawPreviewTexture(preview, _outputPreview, null, ScaleMode.ScaleToFit);
            }
            if (!string.IsNullOrEmpty(_previewError))
                EditorGUILayout.HelpBox(_previewError, MessageType.Warning);
        }

        private void DrawSampler(Texture2D texture, TexturePackSourceKind kind, string role, string label)
        {
            string key = kind == TexturePackSourceKind.DetectedRole ? "role:" + role :
                "manual:" + (texture == null ? 0 : texture.GetEntityId());
            if (!_samplerMasks.TryGetValue(key, out int mask)) mask = 15;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                Rect dragRect;
                using (new EditorGUILayout.HorizontalScope())
                {
                    Rect thumb = GUILayoutUtility.GetRect(42, 42, GUILayout.Width(42));
                    if (texture != null) EditorGUI.DrawPreviewTexture(thumb, texture, null, ScaleMode.ScaleToFit);
                    using (new EditorGUILayout.VerticalScope())
                    {
                        dragRect = GUILayoutUtility.GetRect(new GUIContent(label), EditorStyles.boldLabel);
                        EditorGUI.LabelField(dragRect, label, EditorStyles.boldLabel);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            for (int channel = 0; channel < 4; channel++)
                            {
                                bool enabled = (mask & (1 << channel)) != 0;
                                bool next = GUILayout.Toggle(enabled, ChannelNames[channel], "Button", GUILayout.Width(28));
                                if (next != enabled) mask ^= 1 << channel;
                            }
                        }
                    }
                }
                _samplerMasks[key] = mask;
                var node = new TexturePackNode
                {
                    type = TexturePackNodeType.Sample, sourceKind = kind, sourceRole = role,
                    manualTexture = texture, channelMask = mask
                };
                BeginDrag(dragRect, new DragPayload { Node = node }, "Sample " + label);
                Rect whole = GUILayoutUtility.GetLastRect();
                HandleSamplerObjectDrop(whole);
            }
        }

        private void DrawActionToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Drag actions:", GUILayout.Width(78));
                DrawActionButton(TexturePackNodeType.Desaturate, "Desaturate");
                DrawActionButton(TexturePackNodeType.Levels, "Levels");
                DrawActionButton(TexturePackNodeType.Noise, "Noise");
                DrawActionButton(TexturePackNodeType.Invert, "Invert");
                DrawActionButton(TexturePackNodeType.MultiplyAdd, "Multiply + Add");
                DrawActionButton(TexturePackNodeType.Constant, "Constant");
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_selection.Count == 0))
                    if (GUILayout.Button("Copy", EditorStyles.toolbarButton)) CopySelection();
                using (new EditorGUI.DisabledScope(_clipboard.Count == 0))
                    if (GUILayout.Button("Paste", EditorStyles.toolbarButton)) PasteClipboard();
            }
        }

        private void DrawActionButton(TexturePackNodeType type, string label)
        {
            Rect rect = GUILayoutUtility.GetRect(new GUIContent(label), EditorStyles.toolbarButton);
            if (GUI.Button(rect, label, EditorStyles.toolbarButton)) AddNode(_activeChannel, recipe.channels[_activeChannel].nodes.Count,
                TexturePackNode.Create(type));
            BeginDrag(rect, new DragPayload { Node = TexturePackNode.Create(type) }, label);
        }

        private void DrawOutputStacks()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandWidth(true)))
                    {
                        GUIStyle title = new(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter };
                        EditorGUILayout.LabelField("Output " + ChannelNames[channel], title);
                        _channelScroll[channel] = EditorGUILayout.BeginScrollView(_channelScroll[channel]);
                        DrawDropZone(channel, 0);
                        List<TexturePackNode> nodes = recipe.channels[channel].nodes;
                        for (int index = 0; index < nodes.Count; index++)
                        {
                            DrawNode(channel, index, nodes[index]);
                            DrawDropZone(channel, index + 1);
                        }
                        GUILayout.FlexibleSpace();
                        EditorGUILayout.EndScrollView();
                    }
                }
            }
        }

        private void DrawNode(int channel, int index, TexturePackNode node)
        {
            bool selected = _selection.Contains(node.id);
            Color previous = GUI.backgroundColor;
            if (selected) GUI.backgroundColor = new Color(0.35f, 0.68f, 1f);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUI.backgroundColor = previous;
                Rect header = GUILayoutUtility.GetRect(20, 22, GUILayout.ExpandWidth(true));
                Rect foldout = new(header.x + 2, header.y + 2, 16, 18);
                Rect remove = new(header.xMax - 20, header.y + 1, 19, 19);
                node.expanded = EditorGUI.Foldout(foldout, node.expanded, GUIContent.none);
                EditorGUI.LabelField(new Rect(header.x + 19, header.y, header.width - 42, header.height), NodeTitle(node), EditorStyles.boldLabel);
                if (GUI.Button(remove, "×"))
                {
                    recipe.channels[channel].nodes.RemoveAt(index);
                    _selection.Remove(node.id);
                    Changed();
                    return;
                }
                HandleNodeSelection(header, channel, index, node);
                BeginDrag(header, new DragPayload { NodeIds = DraggedNodeIds(node.id) }, "Move nodes");

                if (node.expanded)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUILayout.VerticalScope(GUILayout.MinWidth(100))) DrawNodeSettings(node);
                        if (_nodePreviews.TryGetValue(node.id, out var preview) && preview != null)
                        {
                            Rect rect = GUILayoutUtility.GetRect(68, 68, GUILayout.Width(68), GUILayout.Height(68));
                            EditorGUI.DrawPreviewTexture(rect, preview, null, ScaleMode.ScaleToFit);
                        }
                    }
                }
            }
        }

        private void DrawNodeSettings(TexturePackNode node)
        {
            EditorGUI.BeginChangeCheck();
            switch (node.type)
            {
                case TexturePackNodeType.Sample:
                    node.sourceKind = (TexturePackSourceKind)EditorGUILayout.EnumPopup(node.sourceKind);
                    if (node.sourceKind == TexturePackSourceKind.DetectedRole)
                    {
                        string[] roles = _sourceSet?.SortedRoles() ?? Array.Empty<string>();
                        int role = Mathf.Max(0, Array.IndexOf(roles, node.sourceRole));
                        role = EditorGUILayout.Popup(role, roles);
                        if (roles.Length > 0) node.sourceRole = roles[Mathf.Clamp(role, 0, roles.Length - 1)];
                    }
                    else node.manualTexture = (Texture2D)EditorGUILayout.ObjectField(node.manualTexture, typeof(Texture2D), false);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            bool on = (node.channelMask & (1 << i)) != 0;
                            bool next = GUILayout.Toggle(on, ChannelNames[i], "Button", GUILayout.Width(25));
                            if (next != on) node.channelMask ^= 1 << i;
                        }
                    }
                    if (node.channelMask == 0) EditorGUILayout.HelpBox("Enable at least one component.", MessageType.Warning);
                    if (CountBits(node.channelMask) > 1)
                        EditorGUILayout.LabelField("Add Desaturate before output.", EditorStyles.miniLabel);
                    break;
                case TexturePackNodeType.Desaturate:
                    EditorGUILayout.LabelField("Linear luminance (green contributes most)", EditorStyles.miniLabel);
                    node.luminanceRed = EditorGUILayout.FloatField("Red", node.luminanceRed);
                    node.luminanceGreen = EditorGUILayout.FloatField("Green", node.luminanceGreen);
                    node.luminanceBlue = EditorGUILayout.FloatField("Blue", node.luminanceBlue);
                    node.normalizeLuminance = EditorGUILayout.Toggle("Normalize", node.normalizeLuminance);
                    break;
                case TexturePackNodeType.Levels:
                    node.inputBlack = EditorGUILayout.Slider("Input black", node.inputBlack, 0, 1);
                    node.gamma = EditorGUILayout.Slider("Gamma", node.gamma, 0.05f, 4);
                    node.inputWhite = EditorGUILayout.Slider("Input white", node.inputWhite, 0, 1);
                    node.outputBlack = EditorGUILayout.Slider("Output black", node.outputBlack, 0, 1);
                    node.outputWhite = EditorGUILayout.Slider("Output white", node.outputWhite, 0, 1);
                    break;
                case TexturePackNodeType.Noise:
                    node.noiseMode = (TexturePackNoiseMode)EditorGUILayout.EnumPopup("Mode", node.noiseMode);
                    node.noiseScale = EditorGUILayout.FloatField("Scale", node.noiseScale);
                    node.noiseSeed = EditorGUILayout.IntField("Seed", node.noiseSeed);
                    node.noiseAmount = EditorGUILayout.Slider("Amount", node.noiseAmount, 0, 1);
                    break;
                case TexturePackNodeType.MultiplyAdd:
                    node.multiply = EditorGUILayout.FloatField("Multiply", node.multiply);
                    node.add = EditorGUILayout.FloatField("Add", node.add);
                    break;
                case TexturePackNodeType.Constant:
                    node.constant = EditorGUILayout.Slider("Value", node.constant, 0, 1);
                    break;
                case TexturePackNodeType.Invert:
                    EditorGUILayout.LabelField("1 − input", EditorStyles.miniLabel);
                    break;
            }
            if (EditorGUI.EndChangeCheck()) Changed();
        }

        private void DrawDropZone(int channel, int index)
        {
            Rect rect = GUILayoutUtility.GetRect(10, 8, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(new Rect(rect.x + 6, rect.center.y, rect.width - 12, 1), new Color(1, 1, 1, 0.09f));
            HandleDrop(rect, channel, index);
        }

        private void HandleDrop(Rect rect, int channel, int index)
        {
            Event current = Event.current;
            if (!rect.Contains(current.mousePosition)) return;
            var payload = DragAndDrop.GetGenericData(DragKey) as DragPayload;
            if (payload == null) return;
            if (current.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Move;
                current.Use();
            }
            else if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                if (payload.Node != null) AddNode(channel, index, payload.Node.Clone());
                else if (payload.NodeIds != null) MoveNodes(payload.NodeIds, channel, index);
                current.Use();
            }
        }

        private void HandleSamplerObjectDrop(Rect rect)
        {
            Event current = Event.current;
            if (!rect.Contains(current.mousePosition) ||
                (current.type != EventType.DragUpdated && current.type != EventType.DragPerform)) return;
            Texture2D texture = DragAndDrop.objectReferences.OfType<Texture2D>().FirstOrDefault();
            if (texture == null) return;
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            if (current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                if (!recipe.manualSources.Contains(texture)) recipe.manualSources.Add(texture);
                Changed();
            }
            current.Use();
        }

        private void HandleNodeSelection(Rect header, int channel, int index, TexturePackNode node)
        {
            Event current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0 || !header.Contains(current.mousePosition)) return;
            if (current.control || current.command)
            {
                if (!_selection.Add(node.id)) _selection.Remove(node.id);
            }
            else if (current.shift && _lastSelectedChannel == channel)
            {
                int start = recipe.channels[channel].nodes.FindIndex(item => item.id == _lastSelectedId);
                if (start >= 0)
                {
                    _selection.Clear();
                    for (int i = Mathf.Min(start, index); i <= Mathf.Max(start, index); i++)
                        _selection.Add(recipe.channels[channel].nodes[i].id);
                }
            }
            else
            {
                _selection.Clear();
                _selection.Add(node.id);
            }
            _lastSelectedId = node.id;
            _lastSelectedChannel = channel;
            _activeChannel = channel;
            Repaint();
        }

        private static void BeginDrag(Rect rect, DragPayload payload, string title)
        {
            Event current = Event.current;
            if (current.type != EventType.MouseDrag || !rect.Contains(current.mousePosition)) return;
            DragAndDrop.PrepareStartDrag();
            DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
            DragAndDrop.SetGenericData(DragKey, payload);
            DragAndDrop.StartDrag(title);
            current.Use();
        }

        private void AddNode(int channel, int index, TexturePackNode node)
        {
            List<TexturePackNode> nodes = recipe.channels[channel].nodes;
            nodes.Insert(Mathf.Clamp(index, 0, nodes.Count), node);
            _selection.Clear();
            _selection.Add(node.id);
            _lastSelectedId = node.id;
            _lastSelectedChannel = _activeChannel = channel;
            Changed();
        }

        private List<string> DraggedNodeIds(string clicked)
        {
            return _selection.Contains(clicked) ? _selection.ToList() : new List<string> { clicked };
        }

        private void MoveNodes(List<string> ids, int destinationChannel, int insertionIndex)
        {
            var ordered = new List<TexturePackNode>();
            int removedBefore = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                List<TexturePackNode> nodes = recipe.channels[channel].nodes;
                for (int i = 0; i < nodes.Count; i++)
                    if (ids.Contains(nodes[i].id))
                    {
                        ordered.Add(nodes[i]);
                        if (channel == destinationChannel && i < insertionIndex) removedBefore++;
                    }
            }
            foreach (var stack in recipe.channels) stack.nodes.RemoveAll(node => ids.Contains(node.id));
            insertionIndex = Mathf.Clamp(insertionIndex - removedBefore, 0, recipe.channels[destinationChannel].nodes.Count);
            recipe.channels[destinationChannel].nodes.InsertRange(insertionIndex, ordered);
            _activeChannel = destinationChannel;
            Changed();
        }

        private void HandleKeyboard()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown || recipe == null || EditorGUIUtility.editingTextField) return;
            bool action = current.control || current.command;
            if (action && current.keyCode == KeyCode.C) { CopySelection(); current.Use(); }
            else if (action && current.keyCode == KeyCode.V) { PasteClipboard(); current.Use(); }
            else if (current.keyCode is KeyCode.Delete or KeyCode.Backspace)
            {
                foreach (var stack in recipe.channels) stack.nodes.RemoveAll(node => _selection.Contains(node.id));
                _selection.Clear();
                Changed();
                current.Use();
            }
        }

        private void CopySelection()
        {
            _clipboard = recipe.channels.SelectMany(stack => stack.nodes).Where(node => _selection.Contains(node.id))
                .Select(node => node.Clone()).ToList();
        }

        private void PasteClipboard()
        {
            if (_clipboard.Count == 0) return;
            List<TexturePackNode> nodes = recipe.channels[_activeChannel].nodes;
            int insertion = nodes.FindLastIndex(node => _selection.Contains(node.id));
            insertion = insertion < 0 ? nodes.Count : insertion + 1;
            var clones = _clipboard.Select(node => node.Clone()).ToList();
            nodes.InsertRange(insertion, clones);
            _selection.Clear();
            foreach (var clone in clones) _selection.Add(clone.id);
            Changed();
        }

        private void EnsureRecipe()
        {
            if (recipe != null) return;
            _transientRecipe = CreateInstance<TexturePackRecipe>();
            _transientRecipe.hideFlags = HideFlags.HideAndDontSave;
            recipe = _transientRecipe;
            if (anchor != null) recipe.ResetTo(TexturePackSourceSet.Detect(anchor));
            else recipe.EnsureChannels();
        }

        private void RefreshSourceSet(bool resetRecipe)
        {
            _sourceSet = TexturePackSourceSet.Detect(anchor);
            EnsureRecipe();
            if (resetRecipe && anchor != null) recipe.ResetTo(_sourceSet);
            Changed();
        }

        private void CreateRecipe()
        {
            if (anchor == null) return;
            string folder = _sourceSet?.Folder ?? "Assets";
            string defaultName = (_sourceSet?.Prefix ?? anchor.name) + " Texture Pack Recipe";
            string path = EditorUtility.SaveFilePanelInProject("Save Texture Pack Recipe", defaultName, "asset",
                "Recipe assets preserve the editable channel stacks.", folder);
            if (string.IsNullOrEmpty(path)) return;
            var created = CreateInstance<TexturePackRecipe>();
            created.CopyFrom(recipe);
            AssetDatabase.CreateAsset(created, path);
            recipe = created;
            _selection.Clear();
            Changed();
        }

        private void Generate()
        {
            try
            {
                string path = TexturePackProcessor.Bake(recipe, anchor, EditorPrefs.GetString(SuffixKey, "_Wet"));
                EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
                ShowNotification(new GUIContent("Generated " + Path.GetFileName(path)));
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Texture Pack Editor", exception.Message, "OK");
            }
        }

        private void Changed()
        {
            if (recipe != null && recipe != _transientRecipe) EditorUtility.SetDirty(recipe);
            _previewDirty = true;
            Repaint();
        }

        private void RebuildPreviews()
        {
            DestroyPreviews();
            _previewDirty = false;
            _previewError = null;
            if (anchor == null || recipe == null) return;
            try
            {
                _sourceSet = TexturePackSourceSet.Detect(anchor);
                using (var session = new TexturePackPixelSession(_sourceSet, 72, 72))
                    for (int channel = 0; channel < 4; channel++)
                        for (int node = 0; node < recipe.channels[channel].nodes.Count; node++)
                            _nodePreviews[recipe.channels[channel].nodes[node].id] =
                                TexturePackProcessor.CreateChannelPreview(recipe.channels[channel], node, session);
                using (var session = new TexturePackPixelSession(_sourceSet, 192, 192))
                    _outputPreview = TexturePackProcessor.CreateOutputPreview(recipe, session);
            }
            catch (Exception exception)
            {
                _previewError = exception.Message;
            }
        }

        private void DestroyPreviews()
        {
            foreach (Texture2D preview in _nodePreviews.Values)
                if (preview != null) DestroyImmediate(preview);
            _nodePreviews.Clear();
            if (_outputPreview != null) DestroyImmediate(_outputPreview);
            _outputPreview = null;
        }

        private static string NodeTitle(TexturePackNode node)
        {
            if (node.type != TexturePackNodeType.Sample) return node.type == TexturePackNodeType.MultiplyAdd ? "Multiply + Add" : node.type.ToString();
            string source = node.sourceKind == TexturePackSourceKind.DetectedRole ? node.sourceRole :
                node.manualTexture == null ? "Missing" : node.manualTexture.name;
            return "Sample " + source + "." + MaskName(node.channelMask);
        }

        private static string MaskName(int mask)
        {
            string value = "";
            for (int i = 0; i < 4; i++) if ((mask & (1 << i)) != 0) value += ChannelNames[i];
            return string.IsNullOrEmpty(value) ? "None" : value;
        }

        private static int CountBits(int value)
        {
            int count = 0;
            for (int i = 0; i < 4; i++) if ((value & (1 << i)) != 0) count++;
            return count;
        }
    }
}
