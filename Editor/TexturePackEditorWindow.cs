using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace TexturePackEditor
{
    public sealed class TexturePackEditorWindow : EditorWindow
    {
        private const string SuffixKey = "TexturePackEditor.SafeOutputSuffix";
        private const string DragKey = "TexturePackEditor.DragPayload";
        private const int PreviewSize = 144;
        private const double PreviewDebounce = 0.18;
        private static readonly string[] ChannelNames = { "R", "G", "B", "A" };
        private static readonly Color[] ChannelColors =
        {
            new(1f, .22f, .2f, .22f), new(.25f, 1f, .35f, .22f),
            new(.2f, .52f, 1f, .22f), new(.75f, .75f, .75f, .2f)
        };
        private static List<TexturePackNode> _clipboard = new();

        [SerializeField] private Texture2D anchor;
        [SerializeField] private TexturePackRecipe recipe;
        [SerializeField] private int activeOutput;
        private TexturePackRecipe _transientRecipe;
        private Texture2D _pendingSource;
        private TexturePackSourceSet _sourceSet;
        private readonly Dictionary<string, int> _samplerMasks = new();
        private readonly HashSet<string> _selection = new();
        private readonly Dictionary<string, Texture2D> _nodePreviews = new();
        private readonly Dictionary<string, float[]> _histograms = new();
        private Texture2D _outputPreview;
        private Vector2 _sourceScroll;
        private Vector2 _outputScroll;
        private Vector2 _toolsScroll;
        private string _lastSelectedId;
        private int _lastSelectedChannel = -1;
        private int _activeChannel;
        private bool _previewDirty = true;
        private double _previewDue;
        private int _previewRevision;
        private Task<TexturePackPreviewResult> _previewTask;
        private TexturePackPixelSession _previewSession;
        private string _previewError;
        private string _sliderKey;
        private int _sliderHandle = -1;

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
            window.minSize = new Vector2(1040, 600);
            window.Show();
        }

        private TexturePackOutput ActiveOutput
        {
            get
            {
                recipe.EnsureOutputs();
                activeOutput = Mathf.Clamp(activeOutput, 0, recipe.outputs.Count - 1);
                return recipe.outputs[activeOutput];
            }
        }

        private void OnEnable()
        {
            Selection.selectionChanged += OnProjectSelectionChanged;
            EditorApplication.update += PreviewUpdate;
            if (anchor == null && Selection.activeObject is Texture2D selected) anchor = selected;
            EnsureRecipe();
            RefreshSourceSet(false);
        }

        private void OnDisable()
        {
            Selection.selectionChanged -= OnProjectSelectionChanged;
            EditorApplication.update -= PreviewUpdate;
            DestroyPreviews();
            if (_previewTask == null) _previewSession?.Dispose();
            else
            {
                TexturePackPixelSession session = _previewSession;
                _previewTask.ContinueWith(_ => session?.Dispose());
            }
            _previewSession = null;
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
            EnsureRecipe();
            HandleKeyboard();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(260))) DrawLeftColumn();
                DrawSeparator();
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true))) DrawCenterColumn();
                DrawSeparator();
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(220))) DrawToolsColumn();
            }
        }

        private void DrawLeftColumn()
        {
            EditorGUILayout.LabelField("Texture Set", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            Texture2D nextAnchor = (Texture2D)EditorGUILayout.ObjectField(anchor, typeof(Texture2D), false);
            if (EditorGUI.EndChangeCheck())
            {
                anchor = nextAnchor;
                RefreshSourceSet(recipe == _transientRecipe);
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var selectedRecipe = (TexturePackRecipe)EditorGUILayout.ObjectField(
                    recipe == _transientRecipe ? null : recipe, typeof(TexturePackRecipe), false);
                if (EditorGUI.EndChangeCheck() && selectedRecipe != null)
                {
                    recipe = selectedRecipe;
                    recipe.EnsureOutputs();
                    activeOutput = 0;
                    Changed();
                }
                string label = recipe == _transientRecipe ? "Save…" : "Copy…";
                if (GUILayout.Button(label, GUILayout.Width(54))) SaveRecipeCopy();
            }

            string suffix = EditorPrefs.GetString(SuffixKey, "_Wet");
            EditorGUI.BeginChangeCheck();
            suffix = EditorGUILayout.TextField("Safe suffix", suffix);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(SuffixKey, suffix);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(recipe == _transientRecipe || anchor == null))
                {
                    if (GUILayout.Button("Generate Tab")) Generate(false);
                    if (GUILayout.Button("Generate All")) Generate(true);
                }
            }
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Sources", EditorStyles.boldLabel);
            _sourceScroll = EditorGUILayout.BeginScrollView(_sourceScroll,
                GUILayout.MaxHeight(Mathf.Max(150, position.height * .42f)));
            if (_sourceSet != null)
                foreach (var pair in _sourceSet.Detected.OrderBy(pair => pair.Key))
                    DrawSourceCard(pair.Value, TexturePackSourceKind.DetectedRole, pair.Key, pair.Key, false);
            foreach (Texture2D texture in recipe.manualSources.ToArray())
                DrawSourceCard(texture, TexturePackSourceKind.ManualTexture, null,
                    texture == null ? "Missing" : texture.name, true);
            DrawEmptySourceCard();
            EditorGUILayout.EndScrollView();

            if (_outputPreview != null)
            {
                EditorGUILayout.LabelField("Output Preview", EditorStyles.boldLabel);
                Rect preview = GUILayoutUtility.GetAspectRect(1, GUILayout.MaxHeight(220));
                EditorGUI.DrawPreviewTexture(preview, _outputPreview, null, ScaleMode.ScaleToFit);
            }
            else
                EditorGUILayout.HelpBox(_previewTask != null ? "Updating preview…" : "Preview unavailable", MessageType.None);
            if (!string.IsNullOrEmpty(_previewError)) EditorGUILayout.HelpBox(_previewError, MessageType.Warning);
        }

        private void DrawCenterColumn()
        {
            DrawOutputTabs();
            TexturePackOutput output = ActiveOutput;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    output.name = EditorGUILayout.TextField("Name", output.name);
                    if (EditorGUI.EndChangeCheck()) MarkRecipeDirty();

                    string[] roles = _sourceSet?.SortedRoles() ?? Array.Empty<string>();
                    int index = Mathf.Max(0, Array.IndexOf(roles, output.outputBaseRole));
                    EditorGUI.BeginChangeCheck();
                    index = EditorGUILayout.Popup("Base", index, roles);
                    if (roles.Length > 0) output.outputBaseRole = roles[Mathf.Clamp(index, 0, roles.Length - 1)];
                    if (EditorGUI.EndChangeCheck()) Changed();
                }
                EditorGUI.BeginChangeCheck();
                output.outputFileName = EditorGUILayout.TextField(
                    new GUIContent("File name", "Optional. Empty keeps the base texture file name."),
                    output.outputFileName);
                if (EditorGUI.EndChangeCheck()) MarkRecipeDirty();
            }

            _outputScroll = EditorGUILayout.BeginScrollView(_outputScroll);
            for (int channel = 0; channel < 4; channel++) DrawChannel(output, channel);
            EditorGUILayout.EndScrollView();
        }

        private void DrawOutputTabs()
        {
            recipe.EnsureOutputs();
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                for (int i = 0; i < recipe.outputs.Count; i++)
                {
                    TexturePackOutput output = recipe.outputs[i];
                    bool current = i == activeOutput;
                    GUIStyle style = current ? EditorStyles.toolbarButton : EditorStyles.toolbarButton;
                    if (GUILayout.Toggle(current, string.IsNullOrWhiteSpace(output.name) ? "Output " + (i + 1) : output.name,
                            style, GUILayout.MinWidth(72)) && !current)
                    {
                        activeOutput = i;
                        _selection.Clear();
                        Changed();
                    }
                }
                if (GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(28))) ShowAddOutputMenu();
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(recipe.outputs.Count <= 1))
                    if (GUILayout.Button("×", EditorStyles.toolbarButton, GUILayout.Width(28)))
                    {
                        recipe.outputs.RemoveAt(activeOutput);
                        activeOutput = Mathf.Clamp(activeOutput, 0, recipe.outputs.Count - 1);
                        _selection.Clear();
                        Changed();
                    }
            }
        }

        private void ShowAddOutputMenu()
        {
            var menu = new GenericMenu();
            string[] roles = _sourceSet?.SortedRoles() ?? Array.Empty<string>();
            foreach (string role in roles)
            {
                string captured = role;
                menu.AddItem(new GUIContent(captured), false, () => AddOutput(captured, captured));
            }
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Custom"), false, () => AddOutput(
                _sourceSet?.AnchorRole, "Custom " + (recipe.outputs.Count + 1)));
            menu.ShowAsContext();
        }

        private void AddOutput(string role, string displayName)
        {
            recipe.outputs.Add(TexturePackOutput.Create(_sourceSet, role, displayName));
            activeOutput = recipe.outputs.Count - 1;
            _selection.Clear();
            Changed();
        }

        private void DrawChannel(TexturePackOutput output, int channel)
        {
            TexturePackChannelStack stack = output.channels[channel];
            Rect header = GUILayoutUtility.GetRect(30, 34, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(header, ChannelColors[channel]);
            GUI.Label(new Rect(header.x + 8, header.y + 7, 22, 20), stack.expanded ? "▼" : "▶", EditorStyles.boldLabel);
            GUI.Label(new Rect(header.x + 31, header.y + 7, 24, 20), ChannelNames[channel], EditorStyles.boldLabel);
            if (!stack.expanded) DrawCollapsedNodeIcons(header, stack);
            Texture2D channelPreview = null;
            if (stack.nodes.Count > 0)
                _nodePreviews.TryGetValue(stack.nodes[^1].id, out channelPreview);
            if (channelPreview != null)
            {
                Rect swatch = new(header.xMax - 31, header.y + 3, 28, 28);
                DrawChannelPreview(swatch, channelPreview, channel);
            }
            HandleDropTarget(header, channel, stack.nodes.Count, true);
            if (Event.current.type == EventType.MouseDown && header.Contains(Event.current.mousePosition))
            {
                stack.expanded = !stack.expanded;
                _activeChannel = channel;
                MarkRecipeDirty();
                Event.current.Use();
            }

            if (!stack.expanded) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                DrawDropZone(channel, 0);
                for (int node = 0; node < stack.nodes.Count; node++)
                {
                    DrawNode(channel, node, stack.nodes[node]);
                    DrawDropZone(channel, node + 1);
                }
                if (stack.nodes.Count == 0)
                    EditorGUILayout.HelpBox("Drag a sampler or tool here.", MessageType.None);
            }
            EditorGUILayout.Space(3);
        }

        private void DrawCollapsedNodeIcons(Rect header, TexturePackChannelStack stack)
        {
            float x = header.x + 58;
            foreach (TexturePackNode node in stack.nodes.Take(9))
            {
                string icon = NodeIcon(node);
                float width = Mathf.Min(70, GUI.skin.label.CalcSize(new GUIContent(icon)).x + 10);
                Rect pill = new(x, header.y + 8, width, 18);
                EditorGUI.DrawRect(pill, NodeColor(node.type));
                GUI.Label(pill, icon, new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter });
                x += width + 4;
                if (x > header.xMax - 70) break;
            }
        }

        private void DrawNode(int channel, int index, TexturePackNode node)
        {
            bool selected = _selection.Contains(node.id);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                Rect header = GUILayoutUtility.GetRect(24, 25, GUILayout.ExpandWidth(true));
                EditorGUI.DrawRect(header, selected ? WithAlpha(NodeColor(node.type), .42f) : NodeColor(node.type));
                Rect fold = new(header.x + 3, header.y + 3, 18, 19);
                Rect remove = new(header.xMax - 23, header.y + 2, 21, 21);
                if (GUI.Button(fold, node.expanded ? "▼" : "▶", EditorStyles.miniButton))
                {
                    node.expanded = !node.expanded;
                    MarkRecipeDirty();
                }
                GUI.Label(new Rect(header.x + 25, header.y + 3, header.width - 54, 20), NodeTitle(node), EditorStyles.boldLabel);
                if (GUI.Button(remove, "×", EditorStyles.miniButton))
                {
                    ActiveOutput.channels[channel].nodes.RemoveAt(index);
                    _selection.Remove(node.id);
                    Changed();
                    return;
                }
                Rect dragArea = new(header.x + 22, header.y, header.width - 48, header.height);
                HandleNodeSelection(dragArea, channel, index, node);
                HandleDragSource(dragArea, new DragPayload { NodeIds = DraggedNodeIds(node.id) }, "Move nodes", null);

                if (node.expanded)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUILayout.VerticalScope()) DrawNodeSettings(node);
                        if (_nodePreviews.TryGetValue(node.id, out Texture2D preview) && preview != null)
                        {
                            Rect previewRect = GUILayoutUtility.GetRect(82, 82, GUILayout.Width(82), GUILayout.Height(82));
                            EditorGUI.DrawPreviewTexture(previewRect, preview, null, ScaleMode.ScaleToFit);
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
                    DrawSampleSettings(node);
                    break;
                case TexturePackNodeType.Desaturate:
                    DrawDesaturateSettings(node);
                    break;
                case TexturePackNodeType.Levels:
                    DrawLevelsSettings(node);
                    break;
                case TexturePackNodeType.Noise:
                    node.noiseMode = (TexturePackNoiseMode)EditorGUILayout.EnumPopup("Mode", node.noiseMode);
                    node.noiseScale = EditorGUILayout.Slider("Scale", node.noiseScale, .05f, 64);
                    node.noiseSeed = EditorGUILayout.IntField("Seed", node.noiseSeed);
                    node.noiseAmount = EditorGUILayout.Slider("Amount", node.noiseAmount, 0, 1);
                    break;
                case TexturePackNodeType.MultiplyAdd:
                    node.multiply = EditorGUILayout.FloatField("Multiply", node.multiply);
                    node.add = EditorGUILayout.FloatField("Add", node.add);
                    break;
                case TexturePackNodeType.Constant:
                    node.constant = DrawColoredSlider("Value", node.constant, Color.white, node.id + ":constant");
                    break;
                case TexturePackNodeType.Invert:
                    EditorGUILayout.LabelField("1 − input", EditorStyles.miniLabel);
                    break;
            }
            if (EditorGUI.EndChangeCheck()) Changed();
        }

        private void DrawSampleSettings(TexturePackNode node)
        {
            node.sourceKind = (TexturePackSourceKind)EditorGUILayout.EnumPopup("Source", node.sourceKind);
            if (node.sourceKind == TexturePackSourceKind.DetectedRole)
            {
                string[] roles = _sourceSet?.SortedRoles() ?? Array.Empty<string>();
                int role = Mathf.Max(0, Array.IndexOf(roles, node.sourceRole));
                role = EditorGUILayout.Popup("Texture", role, roles);
                if (roles.Length > 0) node.sourceRole = roles[Mathf.Clamp(role, 0, roles.Length - 1)];
            }
            else node.manualTexture = (Texture2D)EditorGUILayout.ObjectField("Texture", node.manualTexture, typeof(Texture2D), false);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Channels", GUILayout.Width(58));
                for (int i = 0; i < 4; i++)
                {
                    bool enabled = (node.channelMask & (1 << i)) != 0;
                    bool next = GUILayout.Toggle(enabled, ChannelNames[i], "Button", GUILayout.Width(30));
                    if (next != enabled) node.channelMask ^= 1 << i;
                }
            }
            if (node.channelMask == 0) EditorGUILayout.HelpBox("Enable at least one channel.", MessageType.Warning);
            else if (CountBits(node.channelMask) > 1)
                EditorGUILayout.LabelField("Use Desaturate to reduce multiple channels.", EditorStyles.miniLabel);
        }

        private void DrawDesaturateSettings(TexturePackNode node)
        {
            EditorGUILayout.LabelField("Rec.709 luminance · green contributes most", EditorStyles.miniLabel);
            node.desaturateAmount = DrawGradientSlider("Amount", node.desaturateAmount,
                new Color(.75f, .25f, .65f), Color.gray, node.id + ":amount");
            node.luminanceRed = DrawColoredSlider("Red", node.luminanceRed, Color.red, node.id + ":red");
            node.luminanceGreen = DrawColoredSlider("Green", node.luminanceGreen, Color.green, node.id + ":green");
            node.luminanceBlue = DrawColoredSlider("Blue", node.luminanceBlue, Color.blue, node.id + ":blue");
            node.normalizeLuminance = EditorGUILayout.Toggle("Normalize weights", node.normalizeLuminance);
            EditorGUILayout.LabelField("Output range", EditorStyles.miniLabel);
            DrawTwoPointSlider(node.id + ":desaturateRange", ref node.desaturateBlack,
                ref node.desaturateWhite, null, Color.black, Color.white);
        }

        private void DrawLevelsSettings(TexturePackNode node)
        {
            EditorGUILayout.LabelField("Input Levels", EditorStyles.boldLabel);
            _histograms.TryGetValue(node.id, out float[] histogram);
            DrawThreePointLevels(node, histogram);
            using (new EditorGUILayout.HorizontalScope())
            {
                node.inputBlack = EditorGUILayout.FloatField(node.inputBlack, GUILayout.MaxWidth(70));
                GUILayout.FlexibleSpace();
                node.gamma = EditorGUILayout.FloatField(node.gamma, GUILayout.MaxWidth(70));
                GUILayout.FlexibleSpace();
                node.inputWhite = EditorGUILayout.FloatField(node.inputWhite, GUILayout.MaxWidth(70));
            }
            node.inputBlack = Mathf.Clamp01(node.inputBlack);
            node.inputWhite = Mathf.Clamp(node.inputWhite, node.inputBlack, 1f);
            node.gamma = Mathf.Clamp(node.gamma, .05f, 4f);
            EditorGUILayout.LabelField("Output Levels", EditorStyles.boldLabel);
            DrawTwoPointSlider(node.id + ":levelsOutput", ref node.outputBlack, ref node.outputWhite,
                null, Color.black, Color.white);
        }

        private void DrawToolsColumn()
        {
            EditorGUILayout.LabelField("Tools", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Drag into the open output channel", EditorStyles.miniLabel);
            _toolsScroll = EditorGUILayout.BeginScrollView(_toolsScroll);
            DrawTool(TexturePackNodeType.Desaturate, "Desaturate", "Editable luminance weights and range");
            DrawTool(TexturePackNodeType.Levels, "Levels", "Histogram, black, midpoint and white");
            DrawTool(TexturePackNodeType.Noise, "Noise", "Seeded procedural three-octave noise");
            DrawTool(TexturePackNodeType.Invert, "Invert", "Invert the current signal");
            DrawTool(TexturePackNodeType.MultiplyAdd, "Multiply + Add", "Scale and offset values");
            DrawTool(TexturePackNodeType.Constant, "Constant", "Replace with a fixed value");
            EditorGUILayout.Space(8);
            using (new EditorGUI.DisabledScope(_selection.Count == 0))
            {
                if (GUILayout.Button("Copy Selected")) CopySelection();
                if (GUILayout.Button("Delete Selected")) DeleteSelection();
            }
            using (new EditorGUI.DisabledScope(_clipboard.Count == 0))
                if (GUILayout.Button("Paste into " + ChannelNames[_activeChannel])) PasteClipboard();
            EditorGUILayout.EndScrollView();
        }

        private void DrawTool(TexturePackNodeType type, string title, string description)
        {
            Rect rect = GUILayoutUtility.GetRect(72, 54, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, NodeColor(type));
            GUI.Label(new Rect(rect.x + 9, rect.y + 7, rect.width - 18, 19), title, EditorStyles.boldLabel);
            GUI.Label(new Rect(rect.x + 9, rect.y + 27, rect.width - 18, 18), description, EditorStyles.miniLabel);
            HandleDragSource(rect, new DragPayload { Node = TexturePackNode.Create(type) }, title,
                () => AddNode(_activeChannel, ActiveOutput.channels[_activeChannel].nodes.Count,
                    TexturePackNode.Create(type)));
            EditorGUILayout.Space(4);
        }

        private void DrawSourceCard(Texture2D texture, TexturePackSourceKind kind, string role, string title, bool removable)
        {
            string key = kind == TexturePackSourceKind.DetectedRole ? "role:" + role :
                "manual:" + (texture == null ? "missing" : AssetDatabase.GetAssetPath(texture));
            if (!_samplerMasks.TryGetValue(key, out int mask)) mask = 15;
            Rect card = GUILayoutUtility.GetRect(68, 62, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(card, new Color(.1f, .65f, .8f, .18f));
            Rect thumbnail = new(card.x + 5, card.y + 5, 50, 50);
            if (texture != null) EditorGUI.DrawPreviewTexture(thumbnail, texture, null, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(card.x + 61, card.y + 4, card.width - 86, 18), title, EditorStyles.boldLabel);
            for (int channel = 0; channel < 4; channel++)
            {
                Rect toggle = new(card.x + 61 + channel * 31, card.y + 29, 28, 24);
                bool enabled = (mask & (1 << channel)) != 0;
                bool next = GUI.Toggle(toggle, enabled, ChannelNames[channel], "Button");
                if (next != enabled) mask ^= 1 << channel;
            }
            _samplerMasks[key] = mask;
            if (removable && GUI.Button(new Rect(card.xMax - 22, card.y + 3, 19, 19), "×", EditorStyles.miniButton))
            {
                recipe.manualSources.Remove(texture);
                Changed();
            }
            Rect drag = new(card.x, card.y, card.width, 27);
            var sample = new TexturePackNode
            {
                type = TexturePackNodeType.Sample, sourceKind = kind, sourceRole = role,
                manualTexture = texture, channelMask = mask
            };
            HandleDragSource(drag, new DragPayload { Node = sample }, "Sample " + title, null);
            EditorGUILayout.Space(3);
        }

        private void DrawEmptySourceCard()
        {
            Rect card = GUILayoutUtility.GetRect(68, 62, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(card, new Color(.1f, .65f, .8f, .08f));
            Rect field = new(card.x + 5, card.y + 5, 50, 50);
            EditorGUI.BeginChangeCheck();
            _pendingSource = (Texture2D)EditorGUI.ObjectField(field, _pendingSource, typeof(Texture2D), false);
            GUI.Label(new Rect(card.x + 62, card.y + 11, card.width - 68, 38), "Drop or pick\na texture", EditorStyles.miniLabel);
            if (EditorGUI.EndChangeCheck() && _pendingSource != null)
            {
                if (!recipe.manualSources.Contains(_pendingSource)) recipe.manualSources.Add(_pendingSource);
                _pendingSource = null;
                Changed();
            }
            EditorGUILayout.Space(3);
        }

        private void DrawDropZone(int channel, int index)
        {
            Rect rect = GUILayoutUtility.GetRect(12, 15, GUILayout.ExpandWidth(true));
            HandleDropTarget(rect, channel, index, false);
        }

        private void HandleDropTarget(Rect rect, int channel, int index, bool channelHeader)
        {
            var payload = DragAndDrop.GetGenericData(DragKey) as DragPayload;
            bool hover = payload != null && rect.Contains(Event.current.mousePosition);
            if (!channelHeader)
                EditorGUI.DrawRect(new Rect(rect.x + 8, rect.center.y - (hover ? 2 : 0), rect.width - 16, hover ? 4 : 1),
                    hover ? WithAlpha(ChannelColors[channel], .8f) : new Color(1, 1, 1, .1f));
            else if (hover)
                EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 4, rect.width, 4), WithAlpha(ChannelColors[channel], .9f));
            if (!hover) return;
            if (Event.current.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = payload.Node == null ? DragAndDropVisualMode.Move : DragAndDropVisualMode.Copy;
                Event.current.Use();
            }
            else if (Event.current.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                if (payload.Node != null) AddNode(channel, index, payload.Node.Clone());
                else if (payload.NodeIds != null) MoveNodes(payload.NodeIds, channel, index);
                ActiveOutput.channels[channel].expanded = true;
                Event.current.Use();
            }
        }

        private static void DrawSeparator()
        {
            Rect separator = GUILayoutUtility.GetRect(1, 1, GUILayout.Width(1), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(separator, new Color(1, 1, 1, .12f));
        }

        private void HandleDragSource(Rect rect, DragPayload payload, string title, Action onClick)
        {
            int id = GUIUtility.GetControlID(title.GetHashCode(), FocusType.Passive, rect);
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            {
                GUIUtility.hotControl = id;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                DragAndDrop.PrepareStartDrag();
                DragAndDrop.objectReferences = Array.Empty<UnityEngine.Object>();
                DragAndDrop.SetGenericData(DragKey, payload);
                DragAndDrop.StartDrag(title);
                current.Use();
            }
            else if (current.type == EventType.MouseUp && GUIUtility.hotControl == id)
            {
                GUIUtility.hotControl = 0;
                if (rect.Contains(current.mousePosition)) onClick?.Invoke();
                current.Use();
            }
        }

        private void HandleNodeSelection(Rect rect, int channel, int index, TexturePackNode node)
        {
            Event current = Event.current;
            if (current.type != EventType.MouseDown || current.button != 0 || !rect.Contains(current.mousePosition)) return;
            if (current.control || current.command)
            {
                if (!_selection.Add(node.id)) _selection.Remove(node.id);
            }
            else if (current.shift && _lastSelectedChannel == channel)
            {
                int start = ActiveOutput.channels[channel].nodes.FindIndex(item => item.id == _lastSelectedId);
                if (start >= 0)
                {
                    _selection.Clear();
                    for (int i = Mathf.Min(start, index); i <= Mathf.Max(start, index); i++)
                        _selection.Add(ActiveOutput.channels[channel].nodes[i].id);
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
        }

        private void AddNode(int channel, int index, TexturePackNode node)
        {
            List<TexturePackNode> nodes = ActiveOutput.channels[channel].nodes;
            nodes.Insert(Mathf.Clamp(index, 0, nodes.Count), node);
            _selection.Clear();
            _selection.Add(node.id);
            _lastSelectedId = node.id;
            _lastSelectedChannel = _activeChannel = channel;
            Changed();
        }

        private List<string> DraggedNodeIds(string clicked)
            => _selection.Contains(clicked) ? _selection.ToList() : new List<string> { clicked };

        private void MoveNodes(List<string> ids, int destinationChannel, int insertionIndex)
        {
            var ordered = new List<TexturePackNode>();
            int removedBefore = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                List<TexturePackNode> nodes = ActiveOutput.channels[channel].nodes;
                for (int i = 0; i < nodes.Count; i++)
                    if (ids.Contains(nodes[i].id))
                    {
                        ordered.Add(nodes[i]);
                        if (channel == destinationChannel && i < insertionIndex) removedBefore++;
                    }
            }
            foreach (TexturePackChannelStack stack in ActiveOutput.channels)
                stack.nodes.RemoveAll(node => ids.Contains(node.id));
            List<TexturePackNode> destination = ActiveOutput.channels[destinationChannel].nodes;
            destination.InsertRange(Mathf.Clamp(insertionIndex - removedBefore, 0, destination.Count), ordered);
            _activeChannel = destinationChannel;
            Changed();
        }

        private void HandleKeyboard()
        {
            Event current = Event.current;
            if (current.type != EventType.KeyDown || EditorGUIUtility.editingTextField) return;
            bool action = current.control || current.command;
            if (action && current.keyCode == KeyCode.C) { CopySelection(); current.Use(); }
            else if (action && current.keyCode == KeyCode.V) { PasteClipboard(); current.Use(); }
            else if (current.keyCode is KeyCode.Delete or KeyCode.Backspace) { DeleteSelection(); current.Use(); }
        }

        private void CopySelection()
        {
            _clipboard = ActiveOutput.channels.SelectMany(stack => stack.nodes)
                .Where(node => _selection.Contains(node.id)).Select(node => node.Clone()).ToList();
        }

        private void PasteClipboard()
        {
            if (_clipboard.Count == 0) return;
            List<TexturePackNode> nodes = ActiveOutput.channels[_activeChannel].nodes;
            int insertion = nodes.FindLastIndex(node => _selection.Contains(node.id));
            insertion = insertion < 0 ? nodes.Count : insertion + 1;
            List<TexturePackNode> clones = _clipboard.Select(node => node.Clone()).ToList();
            nodes.InsertRange(insertion, clones);
            _selection.Clear();
            foreach (TexturePackNode clone in clones) _selection.Add(clone.id);
            Changed();
        }

        private void DeleteSelection()
        {
            foreach (TexturePackChannelStack stack in ActiveOutput.channels)
                stack.nodes.RemoveAll(node => _selection.Contains(node.id));
            _selection.Clear();
            Changed();
        }

        private void EnsureRecipe()
        {
            if (recipe != null) { recipe.EnsureOutputs(); return; }
            _transientRecipe = CreateInstance<TexturePackRecipe>();
            _transientRecipe.hideFlags = HideFlags.HideAndDontSave;
            recipe = _transientRecipe;
            if (anchor != null) recipe.ResetTo(TexturePackSourceSet.Detect(anchor));
            else recipe.EnsureOutputs();
        }

        private void RefreshSourceSet(bool reset)
        {
            _sourceSet = TexturePackSourceSet.Detect(anchor);
            EnsureRecipe();
            if (reset && anchor != null) recipe.ResetTo(_sourceSet);
            activeOutput = Mathf.Clamp(activeOutput, 0, recipe.outputs.Count - 1);
            Changed();
        }

        private void SaveRecipeCopy()
        {
            if (anchor == null) return;
            string folder = _sourceSet?.Folder ?? "Assets";
            string defaultName = (_sourceSet?.Prefix ?? anchor.name) + " Texture Pack Recipe";
            string path = EditorUtility.SaveFilePanelInProject("Save Texture Pack Recipe", defaultName,
                "asset", "Recipe assets preserve editable stacks.", folder);
            if (string.IsNullOrEmpty(path)) return;
            var created = CreateInstance<TexturePackRecipe>();
            created.CopyFrom(recipe);
            AssetDatabase.CreateAsset(created, path);
            recipe = created;
            Changed();
        }

        private void Generate(bool all)
        {
            try
            {
                string suffix = EditorPrefs.GetString(SuffixKey, "_Wet");
                int start = all ? 0 : activeOutput;
                int end = all ? recipe.outputs.Count : activeOutput + 1;
                string lastPath = null;
                for (int output = start; output < end; output++)
                    lastPath = TexturePackProcessor.Bake(recipe, output, anchor, suffix);
                if (lastPath != null)
                {
                    EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<Texture2D>(lastPath));
                    ShowNotification(new GUIContent("Generated " + (end - start) + " texture(s)"));
                }
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
            _previewRevision++;
            _previewDue = EditorApplication.timeSinceStartup + PreviewDebounce;
            Repaint();
        }

        private void MarkRecipeDirty()
        {
            if (recipe != null && recipe != _transientRecipe) EditorUtility.SetDirty(recipe);
            Repaint();
        }

        private void PreviewUpdate()
        {
            if (_previewTask != null && _previewTask.IsCompleted)
            {
                TexturePackPreviewResult result = null;
                try
                {
                    if (_previewTask.IsFaulted) _previewError = _previewTask.Exception?.GetBaseException().Message;
                    else result = _previewTask.Result;
                }
                finally
                {
                    _previewSession?.Dispose();
                    _previewSession = null;
                    _previewTask = null;
                }
                if (result != null && result.revision == _previewRevision) ApplyPreview(result);
                Repaint();
            }
            if (_previewTask == null && _previewDirty && EditorApplication.timeSinceStartup >= _previewDue)
                StartPreview();
        }

        private void StartPreview()
        {
            if (anchor == null || recipe == null || recipe.outputs.Count == 0) return;
            _previewDirty = false;
            _previewError = null;
            try
            {
                _sourceSet = TexturePackSourceSet.Detect(anchor);
                TexturePackOutput snapshot = ActiveOutput.Clone(true);
                _previewSession = new TexturePackPixelSession(_sourceSet, PreviewSize, PreviewSize);
                _previewSession.Prepare(snapshot.channels.SelectMany(stack => stack.nodes));
                int revision = _previewRevision;
                TexturePackPixelSession session = _previewSession;
                _previewTask = Task.Run(() => TexturePackPreviewWork.Compute(revision, snapshot, session));
            }
            catch (Exception exception)
            {
                _previewError = exception.Message;
                _previewSession?.Dispose();
                _previewSession = null;
            }
        }

        private void ApplyPreview(TexturePackPreviewResult result)
        {
            DestroyPreviews();
            foreach (var pair in result.nodePixels)
                _nodePreviews[pair.Key] = CreatePreviewTexture(result.width, result.height, pair.Value);
            foreach (var pair in result.histograms) _histograms[pair.Key] = pair.Value;
            _outputPreview = CreatePreviewTexture(result.width, result.height, result.outputPixels);
        }

        private static Texture2D CreatePreviewTexture(int width, int height, Color32[] pixels)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
            { hideFlags = HideFlags.HideAndDontSave };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private void DestroyPreviews()
        {
            foreach (Texture2D preview in _nodePreviews.Values) if (preview != null) DestroyImmediate(preview);
            _nodePreviews.Clear();
            _histograms.Clear();
            if (_outputPreview != null) DestroyImmediate(_outputPreview);
            _outputPreview = null;
        }

        private float DrawColoredSlider(string label, float value, Color color, string key)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(55));
                Rect rail = GUILayoutUtility.GetRect(80, 18, GUILayout.ExpandWidth(true));
                DrawGradient(rail, WithAlpha(Color.black, .55f), WithAlpha(color, .72f));
                return DrawSingleHandle(rail, value, key);
            }
        }

        private float DrawGradientSlider(string label, float value, Color left, Color right, string key)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(label, GUILayout.Width(55));
                Rect rail = GUILayoutUtility.GetRect(80, 18, GUILayout.ExpandWidth(true));
                DrawGradient(rail, WithAlpha(left, .72f), WithAlpha(right, .72f));
                return DrawSingleHandle(rail, value, key);
            }
        }

        private float DrawSingleHandle(Rect rect, float value, string key)
        {
            Rect track = new(rect.x + 5, rect.center.y - 4, rect.width - 10, 8);
            Event current = Event.current;
            if (current.type == EventType.MouseDown && rect.Contains(current.mousePosition))
            {
                _sliderKey = key; _sliderHandle = 0; current.Use();
            }
            if (current.type == EventType.MouseDrag && _sliderKey == key)
            {
                value = Mathf.Clamp01(Mathf.InverseLerp(track.x, track.xMax, current.mousePosition.x));
                GUI.changed = true; current.Use();
            }
            if (current.type == EventType.MouseUp && _sliderKey == key)
            {
                _sliderKey = null; _sliderHandle = -1; current.Use();
            }
            DrawThumb(track, value, Color.white);
            return value;
        }

        private void DrawTwoPointSlider(string key, ref float minimum, ref float maximum,
            float[] histogram, Color left, Color right)
        {
            Rect rect = GUILayoutUtility.GetRect(120, histogram == null ? 28 : 68, GUILayout.ExpandWidth(true));
            if (histogram != null) DrawHistogram(new Rect(rect.x + 5, rect.y, rect.width - 10, rect.height - 20), histogram, Color.white);
            Rect track = new(rect.x + 5, rect.yMax - 17, rect.width - 10, 9);
            DrawGradient(track, left, right);
            float[] values = { minimum, maximum };
            if (HandleMultiSlider(key, track, values))
            {
                minimum = Mathf.Min(values[0], values[1]); maximum = Mathf.Max(values[0], values[1]);
            }
            DrawThumb(track, minimum, Color.black);
            DrawThumb(track, maximum, Color.white);
        }

        private void DrawThreePointLevels(TexturePackNode node, float[] histogram)
        {
            Rect rect = GUILayoutUtility.GetRect(160, 82, GUILayout.ExpandWidth(true));
            Rect histogramRect = new(rect.x + 5, rect.y, rect.width - 10, 58);
            DrawHistogram(histogramRect, histogram, new Color(.8f, .8f, .8f));
            Rect track = new(rect.x + 5, rect.yMax - 18, rect.width - 10, 9);
            DrawGradient(track, Color.black, Color.white);
            float midpoint = Mathf.Lerp(node.inputBlack, node.inputWhite, Mathf.Pow(.5f, node.gamma));
            float[] values = { node.inputBlack, midpoint, node.inputWhite };
            if (HandleMultiSlider(node.id + ":levelsInput", track, values))
            {
                node.inputBlack = Mathf.Min(values[0], values[1]);
                node.inputWhite = Mathf.Max(values[1], values[2]);
                float range = node.inputWhite - node.inputBlack;
                if (range > .0001f)
                {
                    float normalized = Mathf.Clamp((values[1] - node.inputBlack) / range, .0625f, .9659f);
                    node.gamma = Mathf.Clamp(Mathf.Log(normalized) / Mathf.Log(.5f), .05f, 4);
                }
                midpoint = values[1];
            }
            DrawThumb(track, node.inputBlack, Color.black);
            DrawThumb(track, midpoint, Color.gray);
            DrawThumb(track, node.inputWhite, Color.white);
        }

        private bool HandleMultiSlider(string key, Rect track, float[] values)
        {
            bool changed = false;
            Event current = Event.current;
            if (current.type == EventType.MouseDown && new Rect(track.x, track.y - 8, track.width, track.height + 20).Contains(current.mousePosition))
            {
                float normalized = Mathf.InverseLerp(track.x, track.xMax, current.mousePosition.x);
                _sliderHandle = 0;
                for (int i = 1; i < values.Length; i++)
                    if (Mathf.Abs(values[i] - normalized) < Mathf.Abs(values[_sliderHandle] - normalized)) _sliderHandle = i;
                _sliderKey = key;
                current.Use();
            }
            if (current.type == EventType.MouseDrag && _sliderKey == key && _sliderHandle >= 0)
            {
                float value = Mathf.Clamp01(Mathf.InverseLerp(track.x, track.xMax, current.mousePosition.x));
                float low = _sliderHandle == 0 ? 0 : values[_sliderHandle - 1];
                float high = _sliderHandle == values.Length - 1 ? 1 : values[_sliderHandle + 1];
                values[_sliderHandle] = Mathf.Clamp(value, low, high);
                changed = true;
                GUI.changed = true;
                current.Use();
            }
            if (current.type == EventType.MouseUp && _sliderKey == key)
            {
                _sliderKey = null; _sliderHandle = -1; current.Use();
            }
            return changed;
        }

        private static void DrawHistogram(Rect rect, float[] bins, Color color)
        {
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, .28f));
            if (bins == null || bins.Length == 0) return;
            float width = rect.width / bins.Length;
            for (int i = 0; i < bins.Length; i++)
                EditorGUI.DrawRect(new Rect(rect.x + i * width, rect.yMax - bins[i] * rect.height,
                    Mathf.Max(1, width), bins[i] * rect.height), WithAlpha(color, .55f));
        }

        private static void DrawGradient(Rect rect, Color left, Color right)
        {
            const int steps = 48;
            float width = rect.width / steps;
            for (int i = 0; i < steps; i++)
                EditorGUI.DrawRect(new Rect(rect.x + i * width, rect.y, width + 1, rect.height),
                    Color.Lerp(left, right, i / (float)(steps - 1)));
        }

        private static void DrawThumb(Rect track, float value, Color color)
        {
            float x = Mathf.Lerp(track.x, track.xMax, Mathf.Clamp01(value));
            Rect shadow = new(x - 4, track.y - 4, 8, track.height + 8);
            EditorGUI.DrawRect(shadow, new Color(0, 0, 0, .75f));
            EditorGUI.DrawRect(new Rect(shadow.x + 1, shadow.y + 1, shadow.width - 2, shadow.height - 2), color);
        }

        private static void DrawChannelPreview(Rect rect, Texture2D texture, int channel)
        {
            GUI.BeginGroup(rect);
            EditorGUI.DrawPreviewTexture(new Rect(0, 0, rect.width, rect.height), texture, null, ScaleMode.ScaleToFit);
            GUI.Label(new Rect(1, 1, 14, 14), ChannelNames[channel], EditorStyles.miniBoldLabel);
            GUI.EndGroup();
        }

        private static string NodeTitle(TexturePackNode node)
        {
            if (node.type != TexturePackNodeType.Sample)
                return node.type == TexturePackNodeType.MultiplyAdd ? "Multiply + Add" : node.type.ToString();
            string source = node.sourceKind == TexturePackSourceKind.DetectedRole ? node.sourceRole :
                node.manualTexture == null ? "Missing" : node.manualTexture.name;
            return "Sample " + source + "." + MaskName(node.channelMask);
        }

        private static string NodeIcon(TexturePackNode node) => node.type switch
        {
            TexturePackNodeType.Sample => "S:" + (node.sourceKind == TexturePackSourceKind.DetectedRole
                ? node.sourceRole : node.manualTexture == null ? "?" : node.manualTexture.name),
            TexturePackNodeType.Desaturate => "Desat", TexturePackNodeType.Levels => "Levels",
            TexturePackNodeType.MultiplyAdd => "× +", _ => node.type.ToString()
        };

        private static Color NodeColor(TexturePackNodeType type) => type switch
        {
            TexturePackNodeType.Sample => new Color(.08f, .68f, .9f, .22f),
            TexturePackNodeType.Desaturate => new Color(.72f, .3f, .95f, .22f),
            TexturePackNodeType.Levels => new Color(1f, .63f, .08f, .22f),
            TexturePackNodeType.Noise => new Color(.1f, .8f, .58f, .22f),
            TexturePackNodeType.Invert => new Color(.9f, .38f, .62f, .22f),
            TexturePackNodeType.MultiplyAdd => new Color(.45f, .55f, 1f, .22f),
            _ => new Color(.7f, .7f, .7f, .2f)
        };

        private static Color WithAlpha(Color color, float alpha) { color.a = alpha; return color; }
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
