using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.AnimatedValues;
using UnityEngine;

namespace TexturePackEditor
{
    public sealed class TexturePackEditorWindow : EditorWindow
    {
        internal const string SuffixKey = "TexturePackEditor.SafeOutputSuffix";
        private const string DragKey = "TexturePackEditor.DragPayload";
        private const int ScalarSignal = 16;
        private const double PreviewDebounce = 0.18;
        private static readonly string[] ChannelNames = { "R", "G", "B", "A" };
        private static readonly Color[] ChannelColors =
        {
            new(1f, .22f, .2f, .22f), new(.25f, 1f, .35f, .22f),
            new(.2f, .52f, 1f, .22f), new(.75f, .75f, .75f, .2f)
        };
        private static List<TexturePackNode> _clipboard = new();
        private static readonly Dictionary<string, Texture2D> GradientTextures = new();
        private static readonly Dictionary<int, GUIStyle> NodeHeaderStyles = new();
        private static readonly Dictionary<int, GUIStyle> CollapsedNodeStyles = new();
        private static readonly Vector3[] HistogramPoints = new Vector3[256];
        private static GUIStyle _nodeContainerStyle;
        private static GUIStyle _nodeBodyStyle;

        [SerializeField] private Texture2D anchor;
        [SerializeField] private TerrainLayer terrainLayer;
        [SerializeField] private Material sourceMaterial;
        [SerializeField] private List<TexturePackMaterialSlot> materialSlots = new();
        [SerializeField] private bool showMaterialSlots = true;
        [SerializeField] private TexturePackRecipe recipe;
        [SerializeField] private int activeOutput;
        [SerializeField] private float leftColumnWidth = 260;
        [SerializeField] private bool showSelectedNode;
        [SerializeField] private int previewDisplayChannel = -1;
        [SerializeField] private float previewZoom = 1;
        [SerializeField] private Vector2 previewCenter = new(.5f, .5f);
        [SerializeField] private int previewResolution = 256;
        private TexturePackRecipe _transientRecipe;
        private Texture2D _pendingSource;
        private TexturePackSourceSet _sourceSet;
        private readonly Dictionary<string, int> _samplerMasks = new();
        private readonly HashSet<string> _selection = new();
        private readonly Dictionary<string, Texture2D> _nodePreviews = new();
        private readonly Dictionary<string, Color32[]> _nodePreviewPixels = new();
        private readonly Dictionary<string, float[]> _histograms = new();
        private readonly Dictionary<string, AnimBool> _nodeAnimations = new();
        private readonly ConcurrentQueue<TexturePackPreviewUpdate> _previewUpdates = new();
        private readonly Dictionary<string, double> _pendingPreviewNodes = new();
        private double _lastSpinnerRepaint;
        private Texture2D _largePreview;
        private Color32[] _lastOutputPixels;
        private Vector2 _sourceScroll;
        private Vector2 _outputScroll;
        private Vector2 _toolsScroll;
        private string _lastSelectedId;
        private int _lastSelectedChannel = -1;
        private int _activeChannel;
        private bool _previewDirty = true;
        private int _previewDirtyChannels = 15;
        private int _previewInFlightChannels;
        private double _previewDue;
        private int _previewRevision;
        private Task _previewTask;
        private CancellationTokenSource _previewCancellation;
        private TexturePackPixelSession _previewSession;
        private string _previewError;
        private bool _deepBumpWasInstalling;
        private readonly ConcurrentQueue<GenerationProgress> _generationUpdates = new();
        private Task _generationTask;
        private CancellationTokenSource _generationCancellation;
        private TexturePackBakePlan _generationPlan;
        private int[] _generationOutputs;
        private TexturePackOutput[] _generationOutputSnapshots;
        private TexturePackRecipe _generationRecipe;
        private TexturePackRecipe _generationRecipeSnapshot;
        private TexturePackSourceSet _generationSources;
        private Texture2D _generationAnchor;
        private string _generationSuffix;
        private int _generationOutputPosition;
        private float _generationProgress;
        private string _generationStatus;
        private string _generationLastPath;
        private bool _generationActive;
        private string _sliderKey;
        private int _sliderHandle = -1;
        private bool _resizingLeftColumn;
        private bool _previewPanning;
        private Vector2 _previewPanStartMouse;
        private Vector2 _previewPanStartCenter;
        private Rect _lastLargePreviewRect;

        private sealed class DragPayload
        {
            public TexturePackNode Node;
            public List<string> NodeIds;
            public string Label;
            public TexturePackNodeType GhostType;
        }

        private sealed class GenerationProgress
        {
            public float value;
            public string status;
        }

        [MenuItem("Tools/Texture Pack Editor")]
        public static void Open()
        {
            var window = GetWindow<TexturePackEditorWindow>();
            window.titleContent = new GUIContent("Texture Pack Editor");
            window.minSize = new Vector2(1040, 600);
            if ((Selection.activeObject is Texture2D || Selection.activeObject is TerrainLayer || Selection.activeObject is Material) &&
                window.SetInput(Selection.activeObject))
                window.RefreshSourceSet(window.recipe == window._transientRecipe);
            window.Show();
        }

        private bool HasInput => terrainLayer != null || sourceMaterial != null || anchor != null;
        private UnityEngine.Object InputObject => sourceMaterial != null ? sourceMaterial :
            terrainLayer != null ? terrainLayer : anchor;

        private bool SetInput(UnityEngine.Object input)
        {
            if (input != null && input is not Texture2D && input is not TerrainLayer && input is not Material) return false;
            if (sourceMaterial != input as Material) materialSlots.Clear();
            sourceMaterial = input as Material;
            terrainLayer = input as TerrainLayer;
            anchor = input as Texture2D;
            return true;
        }

        private TexturePackSourceSet DetectSources(TexturePackRecipe sourceRecipe)
            => terrainLayer != null ? TexturePackSourceSet.FromTerrainLayer(terrainLayer, sourceRecipe) :
                sourceMaterial != null ? TexturePackSourceSet.FromMaterial(sourceMaterial, sourceRecipe, materialSlots) :
                TexturePackSourceSet.Detect(anchor, sourceRecipe);

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
            Undo.undoRedoPerformed += RecipeUndoRedo;
            EditorApplication.update += PreviewUpdate;
            EnsureRecipe();
            RefreshSourceSet(false);
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= RecipeUndoRedo;
            EditorApplication.update -= PreviewUpdate;
            EditorApplication.delayCall -= StartNextGenerationOutput;
            _previewCancellation?.Cancel();
            _generationCancellation?.Cancel();
            DestroyPreviews();
            ClearPreviewUpdates();
            _pendingPreviewNodes.Clear();
            if (_previewTask == null) _previewSession?.Dispose();
            else
            {
                TexturePackPixelSession session = _previewSession;
                ConcurrentQueue<TexturePackPreviewUpdate> updates = _previewUpdates;
                _previewTask.ContinueWith(completedTask =>
                {
                    session?.Dispose();
                    while (updates.TryDequeue(out TexturePackPreviewUpdate ignored)) { }
                });
            }
            _previewSession = null;
            _previewCancellation?.Dispose();
            _previewCancellation = null;
            if (_generationTask == null) _generationPlan?.Dispose();
            else
            {
                TexturePackBakePlan plan = _generationPlan;
                _generationTask.ContinueWith(completedTask =>
                {
                    _ = completedTask.Exception;
                    plan?.Dispose();
                });
            }
            _generationPlan = null;
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            if (_generationRecipeSnapshot != null) DestroyImmediate(_generationRecipeSnapshot);
            _generationRecipeSnapshot = null;
            _generationSources = null;
            foreach (AnimBool animation in _nodeAnimations.Values)
                animation.valueChanged.RemoveListener(Repaint);
            _nodeAnimations.Clear();
            ReleaseStaticTextures();
            if (recipe != null && recipe != _transientRecipe) AssetDatabase.SaveAssetIfDirty(recipe);
            if (_transientRecipe != null) DestroyImmediate(_transientRecipe);
        }

        private void OnGUI()
        {
            EnsureRecipe();
            RecordRecipeUndo();
            if (Event.current.type == EventType.Layout) PruneNodeAnimations();
            HandleKeyboard();
            DrawGenerationProgress();
            using (new EditorGUILayout.HorizontalScope())
            {
                leftColumnWidth = Mathf.Clamp(leftColumnWidth, 220, Mathf.Max(220, position.width - 520));
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(leftColumnWidth))) DrawLeftColumn();
                DrawLeftResizeHandle();
                using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true))) DrawCenterColumn();
                DrawSeparator();
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(220))) DrawToolsColumn();
            }
            DrawDragGhost();
        }

        private void RecordRecipeUndo()
        {
            if (recipe != null) Undo.RecordObject(recipe, "Edit texture recipe");
        }

        private void RecipeUndoRedo()
        {
            if (recipe == null) return;
            recipe.EnsureOutputs();
            activeOutput = Mathf.Clamp(activeOutput, 0, recipe.outputs.Count - 1);
            RefreshSourceSet(false);
            Changed();
        }

        private void DrawGenerationProgress()
        {
            if (!_generationActive) return;
            Rect row = EditorGUILayout.GetControlRect(false, 24);
            Rect cancel = new(row.xMax - 25, row.y + 1, 24, row.height - 2);
            Rect bar = new(row.x, row.y + 2, Mathf.Max(1, row.width - 30), row.height - 4);
            EditorGUI.ProgressBar(bar, Mathf.Clamp01(_generationProgress),
                string.IsNullOrEmpty(_generationStatus) ? "Preparing…" : _generationStatus);
            if (GUI.Button(cancel, "×", EditorStyles.miniButton))
            {
                _generationStatus = "Cancelling…";
                _generationCancellation?.Cancel();
            }
        }

        private void DrawLeftColumn()
        {
            EditorGUILayout.LabelField("Texture Set", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                UnityEngine.Object input = InputObject;
                UnityEngine.Object nextInput = EditorGUILayout.ObjectField(input, typeof(UnityEngine.Object), false);
                if (EditorGUI.EndChangeCheck())
                {
                    if (SetInput(nextInput)) RefreshSourceSet(recipe == _transientRecipe);
                    else ShowNotification(new GUIContent("Choose a Texture, Material, or Terrain Layer"));
                }
                GUIContent refresh = EditorGUIUtility.IconContent("d_Refresh");
                refresh.tooltip = "Use the selected Project texture, Material, or Terrain Layer, or rescan the current input.";
                if (GUILayout.Button(refresh, EditorStyles.miniButton, GUILayout.Width(26), GUILayout.Height(20)))
                {
                    if (Selection.activeObject is Texture2D || Selection.activeObject is TerrainLayer || Selection.activeObject is Material)
                        SetInput(Selection.activeObject);
                    RefreshSourceSet(recipe == _transientRecipe);
                }
                GUIContent settings = EditorGUIUtility.IconContent("d_Settings");
                settings.tooltip = "Edit texture roles and generation settings.";
                if (GUILayout.Button(settings, EditorStyles.miniButton, GUILayout.Width(26), GUILayout.Height(20)))
                    TexturePackRoleSettingsWindow.Open(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()), () =>
                    {
                        RefreshSourceSet(false);
                        Repaint();
                    });
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
                    SelectFinalPreview();
                    RefreshSourceSet(false);
                }
                string label = recipe == _transientRecipe ? "Save…" : "Copy…";
                if (GUILayout.Button(label, GUILayout.Width(54))) SaveRecipeCopy();
                GUIContent recipeSettings = EditorGUIUtility.IconContent("d_Settings");
                recipeSettings.tooltip = "Edit role rules for this recipe.";
                using (new EditorGUI.DisabledScope(recipe == _transientRecipe))
                    if (GUILayout.Button(recipeSettings, EditorStyles.miniButton,
                            GUILayout.Width(26), GUILayout.Height(20)))
                        TexturePackRecipeSettingsWindow.Open(
                            GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()), recipe, () =>
                        {
                            RefreshSourceSet(false);
                            Repaint();
                        });
            }

            if (sourceMaterial != null) DrawMaterialSlots();

            if (terrainLayer != null || sourceMaterial != null)
                EditorGUILayout.HelpBox("Generation updates the " + (sourceMaterial != null ? "Material" : "Terrain Layer") +
                    " captured when you start, even after switching inputs. Assigned generated textures are overwritten in place.", MessageType.Info);
            bool canGenerateTab = !_generationActive && recipe != _transientRecipe && HasInput &&
                                  IsOutputResolved(ActiveOutput);
            bool canGenerateAny = !_generationActive && recipe != _transientRecipe && HasInput &&
                                  recipe.outputs.Any(IsOutputResolved);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!canGenerateTab))
                    if (GUILayout.Button("Generate Tab")) Generate(false);
                using (new EditorGUI.DisabledScope(!canGenerateAny))
                    if (GUILayout.Button("Generate All")) Generate(true);
            }
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Sources", EditorStyles.boldLabel);
            float desiredPreviewSize = Mathf.Max(120, Mathf.Min(leftColumnWidth - 8, position.height * .55f));
            float sourceHeight = Mathf.Max(120, position.height - desiredPreviewSize - 185);
            _sourceScroll = EditorGUILayout.BeginScrollView(_sourceScroll,
                GUILayout.MaxHeight(sourceHeight));
            if (_sourceSet != null)
                foreach (TexturePackRoleDefinition role in TexturePackProjectSettings.instance.Roles)
                    if (_sourceSet.Detected.TryGetValue(role.id, out Texture2D texture))
                        DrawSourceCard(texture, TexturePackSourceKind.DetectedRole, role.id,
                            texture.name + " [" + role.name + "]", false);
            foreach (Texture2D texture in recipe.manualSources.ToArray())
                DrawSourceCard(texture, TexturePackSourceKind.ManualTexture, null,
                    texture == null ? "Missing" : texture.name, true);
            DrawEmptySourceCard();
            EditorGUILayout.EndScrollView();

            if (_sourceSet?.HasUnresolvedConflicts == true)
                EditorGUILayout.HelpBox(sourceMaterial != null ? "Some roles match multiple material slots. Choose their slots above." :
                    "Some roles match multiple textures. Adjust their role rules.",
                    MessageType.Warning);
            if (_sourceSet?.Unmatched.Count > 0)
                EditorGUILayout.HelpBox("Unassigned: " +
                    string.Join(", ", _sourceSet.Unmatched.Select(texture => texture.name)), MessageType.Info);
            foreach (string error in _sourceSet?.Errors ?? Array.Empty<string>())
                DrawCopyableError(error);

            DrawLargePreview();
            if (!string.IsNullOrEmpty(_previewError)) DrawCopyableError(_previewError);
        }

        private void DrawMaterialSlots()
        {
            showMaterialSlots = EditorGUILayout.Foldout(showMaterialSlots, "Material slots", true);
            if (!showMaterialSlots) return;
            string[] properties = TexturePackMaterialBinding.TextureProperties(sourceMaterial);
            foreach (TexturePackRoleDefinition role in TexturePackProjectSettings.instance.Roles)
            {
                TexturePackMaterialSlot mapping = materialSlots.FirstOrDefault(slot => slot.roleId == role.id);
                int selected = mapping == null ? 0 : string.IsNullOrEmpty(mapping.propertyName) ? 1 :
                    Array.IndexOf(properties, mapping.propertyName) + 2;
                var options = new List<string> { "Automatic" +
                    (_sourceSet?.MaterialProperty(role.id) is string detected && mapping == null ? " (" + detected + ")" : ""), "None" };
                options.AddRange(properties);
                if (mapping != null && !string.IsNullOrEmpty(mapping.propertyName) && selected < 2)
                {
                    selected = options.Count;
                    options.Add("Missing: " + mapping.propertyName);
                }
                int next = EditorGUILayout.Popup(role.name, selected, options.ToArray());
                if (next == selected) continue;
                if (mapping != null) materialSlots.Remove(mapping);
                if (next > 0) materialSlots.Add(new TexturePackMaterialSlot
                    { roleId = role.id, propertyName = next == 1 ? string.Empty : properties[next - 2] });
                RefreshSourceSet(false);
            }
        }

        private void DrawLargePreview()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                int mode = showSelectedNode ? 1 : 0;
                int nextMode = GUILayout.Toolbar(mode, new[] { "Final", "Node" }, EditorStyles.toolbarButton,
                    GUILayout.Width(92));
                if (nextMode != mode && (nextMode == 0 || !string.IsNullOrEmpty(_lastSelectedId)))
                {
                    showSelectedNode = nextMode == 1;
                    RebuildLargePreview();
                }
                GUILayout.FlexibleSpace();
                string[] labels = { "RGBA", "R", "G", "B", "A" };
                for (int index = 0; index < labels.Length; index++)
                {
                    int channel = index - 1;
                    if (GUILayout.Toggle(previewDisplayChannel == channel, labels[index], EditorStyles.toolbarButton,
                            GUILayout.Width(index == 0 ? 42 : 24)) && previewDisplayChannel != channel)
                    {
                        previewDisplayChannel = channel;
                        RebuildLargePreview();
                    }
                }
            }

            float maxZoom = anchor == null ? 64 : Mathf.Clamp(Mathf.Max(anchor.width, anchor.height) / 32f, 1, 128);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                previewZoom = EditorGUILayout.Slider("Zoom", previewZoom, 1, maxZoom);
                if (EditorGUI.EndChangeCheck())
                {
                    ClampPreviewCenter();
                    Changed();
                }
                using (new EditorGUI.DisabledScope(anchor == null))
                    if (GUILayout.Button("1:1", GUILayout.Width(36)))
                    {
                        previewZoom = Mathf.Clamp(Mathf.Max(anchor.width, anchor.height) /
                                                  (float)Mathf.Max(1, previewResolution), 1, maxZoom);
                        ClampPreviewCenter();
                        Changed();
                    }
            }

            float maximumSize = Mathf.Max(120, Mathf.Min(leftColumnWidth - 8, position.height * .55f));
            Rect preview = GUILayoutUtility.GetAspectRect(1, GUILayout.MaxHeight(maximumSize));
            _lastLargePreviewRect = preview;
            EditorGUI.DrawRect(preview, new Color(0, 0, 0, .28f));
            if (_largePreview != null)
                EditorGUI.DrawPreviewTexture(preview, _largePreview, null, ScaleMode.ScaleToFit);
            else
                GUI.Label(preview, _previewTask != null ? "Updating…" : "Preview unavailable",
                    new GUIStyle(EditorStyles.centeredGreyMiniLabel) { alignment = TextAnchor.MiddleCenter });
            HandlePreviewNavigation(preview, maxZoom);
        }

        private void HandlePreviewNavigation(Rect rect, float maxZoom)
        {
            Event current = Event.current;
            if (current.type == EventType.ScrollWheel && rect.Contains(current.mousePosition))
            {
                float oldZoom = previewZoom;
                float nextZoom = Mathf.Clamp(previewZoom * Mathf.Pow(1.12f, -current.delta.y), 1, maxZoom);
                Vector2 local = new(Mathf.InverseLerp(rect.x, rect.xMax, current.mousePosition.x) - .5f,
                    .5f - Mathf.InverseLerp(rect.y, rect.yMax, current.mousePosition.y));
                Vector2 cursorUv = previewCenter + local / oldZoom;
                previewZoom = nextZoom;
                previewCenter = cursorUv - local / previewZoom;
                ClampPreviewCenter();
                Changed();
                current.Use();
            }
            else if (current.type == EventType.MouseDown && current.button == 0 && rect.Contains(current.mousePosition))
            {
                _previewPanning = true;
                _previewPanStartMouse = current.mousePosition;
                _previewPanStartCenter = previewCenter;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && _previewPanning)
            {
                Vector2 delta = current.mousePosition - _previewPanStartMouse;
                previewCenter = _previewPanStartCenter + new Vector2(-delta.x / rect.width,
                    delta.y / rect.height) / previewZoom;
                ClampPreviewCenter();
                Changed();
                current.Use();
            }
            else if (current.type == EventType.MouseUp && _previewPanning)
            {
                _previewPanning = false;
                current.Use();
            }
        }

        private Rect PreviewUvRect()
        {
            float size = 1f / Mathf.Max(1, previewZoom);
            ClampPreviewCenter();
            return new Rect(previewCenter.x - size * .5f, previewCenter.y - size * .5f, size, size);
        }

        private void ClampPreviewCenter()
        {
            float half = .5f / Mathf.Max(1, previewZoom);
            previewCenter.x = Mathf.Clamp(previewCenter.x, half, 1 - half);
            previewCenter.y = Mathf.Clamp(previewCenter.y, half, 1 - half);
        }

        private void RebuildLargePreview()
        {
            Color32[] source = null;
            if (showSelectedNode && !string.IsNullOrEmpty(_lastSelectedId))
                _nodePreviewPixels.TryGetValue(_lastSelectedId, out source);
            else source = _lastOutputPixels;
            int size = source == null ? 0 : Mathf.RoundToInt(Mathf.Sqrt(source.Length));
            if (source == null)
            {
                if (_largePreview != null) DestroyImmediate(_largePreview);
                _largePreview = null;
            }
            else _largePreview = UpdatePreviewTexture(_largePreview, size, size, DisplayPixels(source, previewDisplayChannel));
            Repaint();
        }

        private static Color32[] DisplayPixels(Color32[] source, int channel)
        {
            var result = new Color32[source.Length];
            for (int index = 0; index < source.Length; index++)
            {
                Color32 value = source[index];
                if (channel < 0) result[index] = value;
                else
                {
                    byte component = channel switch { 0 => value.r, 1 => value.g, 2 => value.b, _ => value.a };
                    result[index] = new Color32(component, component, component, 255);
                }
            }
            return result;
        }

        private static Color32[] DownsampleOpaque(Color32[] source, int sourceWidth, int sourceHeight,
            int maximumSize, out int width, out int height)
        {
            float scale = Mathf.Min(1, maximumSize / (float)Mathf.Max(sourceWidth, sourceHeight));
            width = Mathf.Max(1, Mathf.RoundToInt(sourceWidth * scale));
            height = Mathf.Max(1, Mathf.RoundToInt(sourceHeight * scale));
            var result = new Color32[width * height];
            for (int y = 0; y < height; y++)
            {
                int sourceY = Mathf.Min(sourceHeight - 1, (int)((y + .5f) * sourceHeight / height));
                for (int x = 0; x < width; x++)
                {
                    int sourceX = Mathf.Min(sourceWidth - 1, (int)((x + .5f) * sourceWidth / width));
                    Color32 pixel = source[sourceY * sourceWidth + sourceX];
                    result[y * width + x] = new Color32(pixel.r, pixel.g, pixel.b, 255);
                }
            }
            return result;
        }

        private void DrawLeftResizeHandle()
        {
            Rect handle = GUILayoutUtility.GetRect(5, 5, GUILayout.Width(5), GUILayout.ExpandHeight(true));
            EditorGUIUtility.AddCursorRect(handle, MouseCursor.ResizeHorizontal);
            Event current = Event.current;
            if (current.type == EventType.MouseDown && current.button == 0 && handle.Contains(current.mousePosition))
            {
                _resizingLeftColumn = true;
                current.Use();
            }
            else if (current.type == EventType.MouseDrag && _resizingLeftColumn)
            {
                leftColumnWidth = Mathf.Clamp(current.mousePosition.x, 220, Mathf.Max(220, position.width - 520));
                Repaint();
                current.Use();
            }
            else if (current.type == EventType.MouseUp && _resizingLeftColumn)
            {
                _resizingLeftColumn = false;
                int nextResolution = Mathf.Clamp(Mathf.RoundToInt(_lastLargePreviewRect.width / 32) * 32, 128, 512);
                if (nextResolution != previewResolution)
                {
                    previewResolution = nextResolution;
                    Changed();
                }
                current.Use();
            }
            EditorGUI.DrawRect(new Rect(handle.center.x, handle.y, 1, handle.height), new Color(1, 1, 1, .12f));
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

                    EditorGUI.BeginChangeCheck();
                    string outputRole = DrawRolePopup("Base", recipe.EffectiveOutputRole(output));
                    if (EditorGUI.EndChangeCheck())
                    {
                        output.outputBaseRoleId = outputRole;
                        Changed();
                    }
                }
                EditorGUI.BeginChangeCheck();
                string terrainPath = _sourceSet?.BoundOverwritePath(recipe.EffectiveOutputRole(output));
                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(terrainPath)))
                    output.outputFileName = EditorGUILayout.TextField(
                        new GUIContent("File name", "Optional. Existing generated Material and Terrain Layer assignments are reused."),
                        output.outputFileName);
                if (!string.IsNullOrEmpty(terrainPath))
                    EditorGUILayout.LabelField("Replacing", Path.GetFileName(terrainPath));
                if (EditorGUI.EndChangeCheck()) MarkRecipeDirty();
                if (_sourceSet?.CanCreateEmptyRole(recipe.EffectiveOutputRole(output)) == true)
                {
                    int[] sizes = _sourceSet.EmptySlotSizes();
                    int current = _sourceSet.EmptySlotResolution(output);
                    EditorGUI.BeginChangeCheck();
                    int chosen = EditorGUILayout.Popup("Resolution", Array.IndexOf(sizes, current),
                        sizes.Select(size => size + " × " + size).ToArray());
                    if (EditorGUI.EndChangeCheck())
                    {
                        output.emptySlotSize = sizes[chosen];
                        MarkRecipeDirty();
                    }
                }
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
                        SelectFinalPreview();
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
                        SelectFinalPreview();
                        Changed();
                    }
            }
        }

        private void ShowAddOutputMenu()
        {
            var menu = new GenericMenu();
            foreach (TexturePackRoleDefinition role in TexturePackProjectSettings.instance.Roles)
            {
                string roleId = role.id;
                string roleName = role.name;
                menu.AddItem(new GUIContent(roleName), false, () => AddOutput(roleId, roleName));
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
            SelectFinalPreview();
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
                int signal = 0;
                DrawDropZone(channel, 0, signal);
                for (int node = 0; node < stack.nodes.Count; node++)
                {
                    signal = SignalAfter(stack.nodes[node], signal);
                    DrawNode(channel, node, stack.nodes[node]);
                    DrawDropZone(channel, node + 1, signal);
                }
            }
            EditorGUILayout.Space(3);
        }

        private void DrawCollapsedNodeIcons(Rect header, TexturePackChannelStack stack)
        {
            float x = header.x + 58;
            foreach (TexturePackNode node in stack.nodes.Take(9))
            {
                string icon = CollapsedNodeLabel(node);
                GUIContent content = new(icon, NodeTitle(node));
                GUIStyle style = CollapsedNodeStyle(node.type);
                float available = header.xMax - 70 - x;
                if (available < 30) break;
                float width = Mathf.Min(available, Mathf.Min(180, style.CalcSize(content).x + 12));
                bool waiting = NodePreviewBusy(node.id, EditorApplication.timeSinceStartup);
                if (waiting) width = Mathf.Min(available, width + 18);
                Rect pill = new(x, header.y + 8, width, 18);
                GUI.Label(pill, waiting ? new GUIContent(icon + "   ", content.tooltip) : content, style);
                if (waiting) DrawPreviewSpinner(new Rect(pill.xMax - 17, pill.y + 1, 16, 16));
                x += width + 4;
            }
        }

        private void DrawNode(int channel, int index, TexturePackNode node)
        {
            bool selected = _selection.Contains(node.id);
            AnimBool animation = NodeAnimation(node);
            animation.target = node.expanded;
            using (new EditorGUILayout.VerticalScope(NodeContainerStyle))
            {
                Rect header = GUILayoutUtility.GetRect(27, 28, GUILayout.ExpandWidth(true));
                GUI.Box(header, GUIContent.none, NodeHeaderStyle(node.type, selected));
                Rect fold = new(header.x + 3, header.y + 4, 18, 19);
                Rect remove = new(header.xMax - 23, header.y + 2, 21, 21);
                if (GUI.Button(fold, node.expanded ? "▼" : "▶", EditorStyles.miniButton))
                {
                    node.expanded = !node.expanded;
                    animation.target = node.expanded;
                    MarkRecipeDirty();
                }
                GUI.Label(new Rect(header.x + 25, header.y + 4, header.width - 76, 20),
                    new GUIContent(NodeTitle(node), NodeTooltip(node.type)), EditorStyles.boldLabel);
                if (NodePreviewBusy(node.id, EditorApplication.timeSinceStartup))
                    DrawPreviewSpinner(new Rect(header.xMax - 43, header.y + 5, 16, 16));
                if (GUI.Button(remove, "×", EditorStyles.miniButton))
                {
                    ActiveOutput.channels[channel].nodes.RemoveAt(index);
                    _selection.Remove(node.id);
                    RemoveNodeAnimation(node.id);
                    if (_lastSelectedId == node.id) SelectFinalPreview();
                    Changed(1 << channel);
                    return;
                }
                Rect dragArea = new(header.x + 22, header.y, header.width - 48, header.height);
                HandleNodeSelection(dragArea, channel, index, node);
                List<string> draggedIds = DraggedNodeIds(node.id);
                string dragLabel = draggedIds.Count == 1 ? NodeTitle(node) : draggedIds.Count + " nodes";
                HandleDragSource(dragArea, new DragPayload
                    { NodeIds = draggedIds, Label = dragLabel, GhostType = node.type }, dragLabel, null);

                if (EditorGUILayout.BeginFadeGroup(animation.faded))
                {
                    using (new EditorGUILayout.VerticalScope(NodeBodyStyle))
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUILayout.VerticalScope()) DrawNodeSettings(node, channel);
                        if (_nodePreviews.TryGetValue(node.id, out Texture2D preview) && preview != null)
                        {
                            Rect previewRect = GUILayoutUtility.GetRect(82, 82, GUILayout.Width(82), GUILayout.Height(82));
                            EditorGUI.DrawPreviewTexture(previewRect, preview, null, ScaleMode.ScaleToFit);
                        }
                    }
                }
                EditorGUILayout.EndFadeGroup();
                GUILayout.Space(4 * (1 - animation.faded));
            }
        }

        private void DrawNodeSettings(TexturePackNode node, int channel)
        {
            RecordRecipeUndo();
            EditorGUI.BeginChangeCheck();
            switch (node.type)
            {
                case TexturePackNodeType.Height:
                    DrawHeightSettings(node);
                    break;
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
                    DrawBlendSettings(node);
                    break;
            }
            if (EditorGUI.EndChangeCheck())
            {
                _lastSelectedId = node.id;
                _lastSelectedChannel = _activeChannel = channel;
                showSelectedNode = true;
                Changed(1 << channel);
            }
        }

        private AnimBool NodeAnimation(TexturePackNode node)
        {
            if (_nodeAnimations.TryGetValue(node.id, out AnimBool animation)) return animation;
            animation = new AnimBool(node.expanded) { speed = 5.5f };
            animation.valueChanged.AddListener(Repaint);
            _nodeAnimations.Add(node.id, animation);
            return animation;
        }

        private void RemoveNodeAnimation(string nodeId)
        {
            if (!_nodeAnimations.TryGetValue(nodeId, out AnimBool animation)) return;
            animation.valueChanged.RemoveListener(Repaint);
            _nodeAnimations.Remove(nodeId);
        }

        private void PruneNodeAnimations()
        {
            if (_nodeAnimations.Count == 0 || recipe == null) return;
            var valid = new HashSet<string>(recipe.outputs.SelectMany(output => output.channels)
                .SelectMany(channel => channel.nodes).Select(node => node.id));
            foreach (string nodeId in _nodeAnimations.Keys.Where(nodeId => !valid.Contains(nodeId)).ToArray())
                RemoveNodeAnimation(nodeId);
        }

        private void DrawSampleSettings(TexturePackNode node)
        {
            DrawSourceSettings(node);
            DrawSampleChannels(node);
        }

        private void DrawSourceSettings(TexturePackNode node)
        {
            node.sourceKind = (TexturePackSourceKind)EditorGUILayout.EnumPopup("Source", node.sourceKind);
            if (node.sourceKind == TexturePackSourceKind.DetectedRole)
            {
                string role = DrawRolePopup("Texture", recipe.EffectiveRole(node));
                if (role != recipe.EffectiveRole(node)) node.sourceRoleId = role;
            }
            else node.manualTexture = (Texture2D)EditorGUILayout.ObjectField("Texture", node.manualTexture, typeof(Texture2D), false);
        }

        private void DrawHeightSettings(TexturePackNode node)
        {
            var previousMode = node.heightMode;
            node.heightMode = (TexturePackHeightMode)EditorGUILayout.EnumPopup("Mode", node.heightMode);
            if (node.heightMode != previousMode && node.sourceKind == TexturePackSourceKind.DetectedRole)
            {
                string role = node.heightMode == TexturePackHeightMode.NormalIntegration ? "normal" : "basemap";
                node.sourceRoleId = role;
                node.sourceRole = role;
            }
            DrawSourceSettings(node);
            node.heightResolution = EditorGUILayout.IntPopup("Solve resolution", node.heightResolution,
                new[] { "128", "256", "512", "1024", "2048" }, new[] { 128, 256, 512, 1024, 2048 });
            node.heightSeamless = EditorGUILayout.Toggle("Seamless", node.heightSeamless);
            if (node.heightMode == TexturePackHeightMode.DeepBump)
            {
                TexturePackDeepBump.CheckInstallation();
                bool busy = TexturePackDeepBump.Installing || TexturePackDeepBump.Checking || TexturePackDeepBump.Cleaning;
                EditorGUILayout.LabelField("DeepBump", TexturePackDeepBump.Checking ? "Checking installation…" :
                    TexturePackDeepBump.Healthy ? "Ready · local CPU" : "Setup or repair required");
                using (new EditorGUI.DisabledScope(busy))
                {
                    string python = EditorGUILayout.TextField("Setup Python", TexturePackDeepBump.Python);
                    if (python != TexturePackDeepBump.Python) TexturePackDeepBump.Python = python;
                    if (GUILayout.Button("Choose Python…"))
                    {
                        string selected = EditorUtility.OpenFilePanel("Choose Python 3.10–3.13", "", "");
                        if (!string.IsNullOrEmpty(selected)) TexturePackDeepBump.Python = selected;
                    }
                    if (GUILayout.Button(TexturePackDeepBump.Ready ? "Repair DeepBump setup" : "Install DeepBump")) TexturePackDeepBump.Install();
                    using (new EditorGUI.DisabledScope(!TexturePackDeepBump.Ready))
                        if (GUILayout.Button("Check installation")) TexturePackDeepBump.CheckInstallation(true);
                    if (GUILayout.Button("Clean unused installations")) TexturePackDeepBump.CleanupUnusedInstallations();
                }
                if (TexturePackDeepBump.Installing && GUILayout.Button("Cancel setup")) TexturePackDeepBump.CancelSetup();
                if (busy) Repaint();
                if (!string.IsNullOrEmpty(TexturePackDeepBump.HealthError)) DrawCopyableError(TexturePackDeepBump.HealthError);
                if (!string.IsNullOrEmpty(TexturePackDeepBump.Status))
                {
                    if (TexturePackDeepBump.HasError) DrawCopyableError(TexturePackDeepBump.Status);
                    else EditorGUILayout.HelpBox(TexturePackDeepBump.Status, MessageType.None);
                }
            }
            if (node.heightMode == TexturePackHeightMode.MultiscaleAlbedo)
            {
                node.heightCoarse = EditorGUILayout.Slider("Coarse", node.heightCoarse, 0, 2);
                node.heightMedium = EditorGUILayout.Slider("Medium", node.heightMedium, 0, 2);
                node.heightFine = EditorGUILayout.Slider("Fine", node.heightFine, 0, 2);
                node.heightRemoveLighting = EditorGUILayout.Toggle("Remove broad lighting", node.heightRemoveLighting);
            }
            else node.heightFlipY = EditorGUILayout.Toggle("Flip normal Y", node.heightFlipY);
            node.heightStrength = EditorGUILayout.Slider("Strength", node.heightStrength, -2, 2);
            node.heightCenter = EditorGUILayout.Slider("Center", node.heightCenter, 0, 1);
            DrawBlendSettings(node);
            EditorGUILayout.HelpBox(node.heightMode == TexturePackHeightMode.MultiscaleAlbedo
                ? "Estimates relief from brightness at several scales. Painted color and lighting can be mistaken for height."
                : node.heightMode == TexturePackHeightMode.DeepBump ? "Predicts normals from albedo, then integrates them into relative height. Processing stays on this computer."
                : "Reads full RGB directly from its source. Reconstructs relative height; absolute depth is not stored in a normal map.", MessageType.Info);
        }

        private void DrawSampleChannels(TexturePackNode node)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(new GUIContent("Channels",
                    "One selected component becomes a scalar. Multiple selected components remain separate until a full Desaturate."),
                    GUILayout.Width(58));
                for (int i = 0; i < 4; i++)
                {
                    bool enabled = (node.channelMask & (1 << i)) != 0;
                    bool next = GUILayout.Toggle(enabled, ChannelNames[i], "Button", GUILayout.Width(30));
                    if (next != enabled) node.channelMask ^= 1 << i;
                }
            }
            if (node.channelMask == 0) EditorGUILayout.HelpBox("Enable at least one channel.", MessageType.Warning);
            node.sampleDesaturate = EditorGUILayout.Toggle("Desaturate", node.sampleDesaturate);
            if (node.sampleDesaturate) DrawDesaturateSettings(node);
            DrawBlendSettings(node);
        }

        private static void DrawBlendSettings(TexturePackNode node)
        {
            node.blendMode = (TexturePackBlendMode)EditorGUILayout.EnumPopup("Blend", node.blendMode);
            node.blendAmount = EditorGUILayout.Slider("Blend amount", node.blendAmount, 0, 1);
        }

        private void DrawCopyableError(string message)
        {
            string display = message;
            if (display.Length > 400)
            {
                string[] lines = display.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                display = lines.Length > 0 ? lines[lines.Length - 1] : display;
                if (display.Length > 400) display = display.Substring(0, 397) + "…";
            }
            EditorGUILayout.HelpBox(display + "\nClick to copy the full error.", MessageType.Warning);
            Rect rect = GUILayoutUtility.GetLastRect();
            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && rect.Contains(Event.current.mousePosition))
            {
                EditorGUIUtility.systemCopyBuffer = message;
                ShowNotification(new GUIContent("Error copied to clipboard"));
                Event.current.Use();
            }
        }

        private string DrawRolePopup(string label, string roleId)
        {
            IReadOnlyList<TexturePackRoleDefinition> roles = TexturePackProjectSettings.instance.Roles;
            if (roles.Count == 0)
            {
                EditorGUILayout.LabelField(label, "No roles configured");
                return roleId;
            }
            int current = -1;
            var roleLabels = new GUIContent[roles.Count];
            for (int index = 0; index < roles.Count; index++)
            {
                TexturePackRoleDefinition role = roles[index];
                if (string.Equals(role.id, roleId, StringComparison.OrdinalIgnoreCase)) current = index;
                bool resolved = _sourceSet?.ResolveRole(role.id) != null;
                roleLabels[index] = new GUIContent(resolved ? role.name : role.name + " (missing)");
            }
            if (current >= 0)
            {
                int next = EditorGUILayout.Popup(new GUIContent(label), current, roleLabels);
                return next == current ? roleId : roles[Mathf.Clamp(next, 0, roles.Count - 1)].id;
            }
            var missingLabels = new GUIContent[roleLabels.Length + 1];
            missingLabels[0] = new GUIContent("Missing: " + (string.IsNullOrEmpty(roleId) ? "unassigned" : roleId));
            Array.Copy(roleLabels, 0, missingLabels, 1, roleLabels.Length);
            int selected = EditorGUILayout.Popup(new GUIContent(label), 0, missingLabels);
            return selected == 0 ? roleId : roles[selected - 1].id;
        }

        private bool IsOutputResolved(TexturePackOutput output)
        {
            if (_sourceSet == null || output == null) return false;
            string outputRole = recipe.EffectiveOutputRole(output);
            if (_sourceSet.ResolveRole(outputRole) == null && !_sourceSet.CanCreateEmptyRole(outputRole)) return false;
            foreach (TexturePackNode node in output.channels.SelectMany(channel => channel.nodes))
            {
                if (node.type != TexturePackNodeType.Sample && node.type != TexturePackNodeType.Height) continue;
                if (node.type == TexturePackNodeType.Height && _sourceSet.Resolve(node) == null) return false;
                if (node.sourceKind == TexturePackSourceKind.ManualTexture)
                {
                    if (node.manualTexture == null) return false;
                }
                else if (_sourceSet.ResolveRole(recipe.EffectiveRole(node)) == null &&
                         !_sourceSet.CanCreateEmptyRole(recipe.EffectiveRole(node))) return false;
            }
            return true;
        }

        private void DrawDesaturateSettings(TexturePackNode node)
        {
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
            _toolsScroll = EditorGUILayout.BeginScrollView(_toolsScroll);
            DrawTool(TexturePackNodeType.Height, "Height", "Reconstruct height from a full RGB source");
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
            Rect rect = GUILayoutUtility.GetRect(42, 36, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, NodeColor(type));
            GUI.Label(new Rect(rect.x + 9, rect.y + 8, rect.width - 18, 19),
                new GUIContent(title, description), EditorStyles.boldLabel);
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
            GUI.Label(new Rect(card.x + 61, card.y + 4, card.width - 86, 18),
                new GUIContent(title, title), EditorStyles.boldLabel);
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
                MarkRecipeDirty();
            }
            Rect drag = new(card.x, card.y, card.width, 27);
            var sample = new TexturePackNode
            {
                type = TexturePackNodeType.Sample, sourceKind = kind, sourceRole = role, sourceRoleId = role,
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
                MarkRecipeDirty();
            }
            EditorGUILayout.Space(3);
        }

        private void DrawDropZone(int channel, int index, int signal)
        {
            Rect rect = GUILayoutUtility.GetRect(20, 24, GUILayout.ExpandWidth(true));
            DrawSignalWires(rect, signal);
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
                DragAndDrop.SetGenericData(DragKey, null);
                Event.current.Use();
            }
        }

        private void DrawDragGhost()
        {
            Event current = Event.current;
            if (current.type == EventType.DragExited)
            {
                DragAndDrop.SetGenericData(DragKey, null);
                Repaint();
                return;
            }
            var payload = DragAndDrop.GetGenericData(DragKey) as DragPayload;
            if (payload == null) return;
            if (current.type == EventType.DragUpdated) Repaint();
            if (current.type != EventType.Repaint) return;
            string label = string.IsNullOrEmpty(payload.Label) ? "Node" : payload.Label;
            GUIStyle style = CollapsedNodeStyle(payload.GhostType);
            float width = Mathf.Clamp(style.CalcSize(new GUIContent(label)).x + 16, 90, 220);
            Vector2 mouse = current.mousePosition + new Vector2(14, 12);
            Rect ghost = new(Mathf.Min(mouse.x, position.width - width - 6),
                Mathf.Min(mouse.y, position.height - 26), width, 22);
            Color previous = GUI.color;
            GUI.color = new Color(1, 1, 1, .72f);
            GUI.Label(ghost, new GUIContent(label), style);
            GUI.color = previous;
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
                payload.Label ??= title;
                if (payload.Node != null) payload.GhostType = payload.Node.type;
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
            showSelectedNode = true;
            RebuildLargePreview();
        }

        private void AddNode(int channel, int index, TexturePackNode node)
        {
            List<TexturePackNode> nodes = ActiveOutput.channels[channel].nodes;
            nodes.Insert(Mathf.Clamp(index, 0, nodes.Count), node);
            _selection.Clear();
            _selection.Add(node.id);
            _lastSelectedId = node.id;
            _lastSelectedChannel = _activeChannel = channel;
            showSelectedNode = true;
            Changed(1 << channel);
        }

        private List<string> DraggedNodeIds(string clicked)
            => _selection.Contains(clicked) ? _selection.ToList() : new List<string> { clicked };

        private void MoveNodes(List<string> ids, int destinationChannel, int insertionIndex)
        {
            var ordered = new List<TexturePackNode>();
            int removedBefore = 0;
            int dirtyChannels = 1 << destinationChannel;
            for (int channel = 0; channel < 4; channel++)
            {
                List<TexturePackNode> nodes = ActiveOutput.channels[channel].nodes;
                for (int i = 0; i < nodes.Count; i++)
                    if (ids.Contains(nodes[i].id))
                    {
                        ordered.Add(nodes[i]);
                        dirtyChannels |= 1 << channel;
                        if (channel == destinationChannel && i < insertionIndex) removedBefore++;
                    }
            }
            foreach (TexturePackChannelStack stack in ActiveOutput.channels)
                stack.nodes.RemoveAll(node => ids.Contains(node.id));
            List<TexturePackNode> destination = ActiveOutput.channels[destinationChannel].nodes;
            destination.InsertRange(Mathf.Clamp(insertionIndex - removedBefore, 0, destination.Count), ordered);
            _activeChannel = destinationChannel;
            Changed(dirtyChannels);
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
            Changed(1 << _activeChannel);
        }

        private void DeleteSelection()
        {
            bool removedPreviewNode = !string.IsNullOrEmpty(_lastSelectedId) && _selection.Contains(_lastSelectedId);
            int dirtyChannels = 0;
            for (int channel = 0; channel < ActiveOutput.channels.Count; channel++)
                if (ActiveOutput.channels[channel].nodes.RemoveAll(node => _selection.Contains(node.id)) > 0)
                    dirtyChannels |= 1 << channel;
            _selection.Clear();
            if (removedPreviewNode) SelectFinalPreview();
            if (dirtyChannels != 0) Changed(dirtyChannels);
        }

        private void SelectFinalPreview()
        {
            _lastSelectedId = null;
            _lastSelectedChannel = -1;
            showSelectedNode = false;
            RebuildLargePreview();
        }

        private void EnsureRecipe()
        {
            if (recipe != null) { recipe.EnsureOutputs(); return; }
            _transientRecipe = CreateInstance<TexturePackRecipe>();
            _transientRecipe.hideFlags = HideFlags.HideAndDontSave;
            recipe = _transientRecipe;
            if (HasInput) recipe.ResetTo(DetectSources(recipe));
            else recipe.EnsureOutputs();
        }

        private void RefreshSourceSet(bool reset)
        {
            EnsureRecipe();
            _sourceSet = DetectSources(recipe);
            if (_sourceSet.HasBoundTarget) anchor = _sourceSet.ResolveRole(_sourceSet.AnchorRole);
            if (reset && HasInput) recipe.ResetTo(_sourceSet);
            activeOutput = Mathf.Clamp(activeOutput, 0, recipe.outputs.Count - 1);
            Changed();
        }

        private void SaveRecipeCopy()
        {
            if (!HasInput) return;
            string folder = _sourceSet?.Folder ?? "Assets";
            string defaultName = (_sourceSet?.Prefix ?? InputObject.name) + " Texture Pack Recipe";
            string path = EditorUtility.SaveFilePanelInProject("Save Texture Pack Recipe", defaultName,
                "asset", "Recipe assets preserve editable stacks.", folder);
            if (string.IsNullOrEmpty(path)) return;
            var created = CreateInstance<TexturePackRecipe>();
            created.CopyFrom(recipe);
            AssetDatabase.CreateAsset(created, path);
            recipe = created;
            Changed();
        }

        private bool ResolveOutputConflicts(string suffix)
        {
            if (_sourceSet.HasBoundTarget)
            {
                var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < recipe.outputs.Count; index++)
                {
                    string role = recipe.EffectiveOutputRole(recipe.outputs[index]);
                    string slot = _sourceSet.BoundSlot(role);
                    if (slot == null || slots.Add(slot)) continue;
                    EditorUtility.DisplayDialog("Texture slot conflict",
                        "Multiple output tabs target the " + slot +
                        " slot. Keep one output per slot, or change the extra tab's Base role or material slot mapping.", "Edit conflicting tab");
                    activeOutput = index;
                    SelectFinalPreview();
                    Changed();
                    return false;
                }
            }
            string recipeGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(recipe));
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changes = new List<(int index, string name, string before, string after)>();
            // Include other tabs even for Generate Tab, so one tab cannot claim another's name.
            for (int index = 0; index < recipe.outputs.Count; index++)
            {
                TexturePackOutput output = recipe.outputs[index];
                if (_sourceSet.ResolveRole(recipe.EffectiveOutputRole(output)) == null &&
                    !_sourceSet.CanCreateEmptyRole(recipe.EffectiveOutputRole(output))) continue;
                string candidate = TexturePackProcessor.OutputCandidate(recipe, output, _sourceSet, suffix);
                string terrainPath = _sourceSet.BoundOverwritePath(recipe.EffectiveOutputRole(output));
                if (!reserved.Contains(candidate) &&
                    !TexturePackProcessor.HasOutputConflict(candidate, recipeGuid, output.id, terrainPath))
                {
                    reserved.Add(candidate);
                    continue;
                }
                if (!string.IsNullOrEmpty(terrainPath))
                {
                    EditorUtility.DisplayDialog("Assigned texture conflict",
                        "Multiple outputs target " + candidate + ". Assign distinct textures to the input's slots " +
                        "or remove the conflicting output tab before generating.", "OK");
                    return false;
                }
                TexturePackOutput proposed = output.Clone(true);
                string baseName = string.IsNullOrWhiteSpace(output.outputFileName)
                    ? _sourceSet.CanCreateEmptyRole(recipe.EffectiveOutputRole(output))
                        ? _sourceSet.Prefix + "_" + recipe.EffectiveOutputRole(output)
                        : Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(
                            _sourceSet.ResolveRole(recipe.EffectiveOutputRole(output)))) : output.outputFileName;
                int number = 2;
                string unique;
                do
                {
                    proposed.outputFileName = baseName + "_" + number++;
                    unique = TexturePackProcessor.OutputCandidate(recipe, proposed, _sourceSet, suffix);
                } while (reserved.Contains(unique) ||
                         TexturePackProcessor.HasOutputConflict(unique, recipeGuid, output.id));
                reserved.Add(unique);
                changes.Add((index, proposed.outputFileName, candidate, unique));
            }
            if (changes.Count == 0) return true;
            string details = string.Join("\n\n", changes.Select(change =>
                recipe.outputs[change.index].name + ":\n" + change.before + "\n→ " + change.after));
            int choice = EditorUtility.DisplayDialogComplex("Output filename conflicts",
                "These destinations overlap another tab or an existing file that this output does not own. " +
                "Use the proposed names, or edit each tab's File name before generating.\n\n" + details,
                "Use unique names", "Cancel", "Edit filenames");
            if (choice != 0)
            {
                if (choice == 2)
                {
                    activeOutput = changes[0].index;
                    _selection.Clear();
                    SelectFinalPreview();
                    Changed();
                }
                return false;
            }
            Undo.RecordObject(recipe, "Resolve output filename conflicts");
            foreach (var change in changes) recipe.outputs[change.index].outputFileName = change.name;
            EditorUtility.SetDirty(recipe);
            AssetDatabase.SaveAssetIfDirty(recipe);
            return true;
        }

        private void Generate(bool all)
        {
            if (_generationActive) return;
            try
            {
                _sourceSet = DetectSources(recipe);
                string suffix = EditorPrefs.GetString(SuffixKey, "_Wet");
                if (!ResolveOutputConflicts(suffix)) return;
                _generationOutputs = all
                    ? Enumerable.Range(0, recipe.outputs.Count).Where(index => IsOutputResolved(recipe.outputs[index])).ToArray()
                    : new[] { activeOutput };
                if (_generationOutputs.Length == 0)
                    throw new InvalidOperationException("No outputs have all required roles resolved.");
                _generationOutputSnapshots = _generationOutputs
                    .Select(output => recipe.outputs[output].Clone(true)).ToArray();
                _generationRecipe = recipe;
                _generationRecipeSnapshot = Instantiate(recipe);
                _generationRecipeSnapshot.hideFlags = HideFlags.HideAndDontSave;
                _generationSources = DetectSources(_generationRecipeSnapshot);
                _generationAnchor = anchor;
                _generationSuffix = suffix;
                _generationOutputPosition = 0;
                _generationProgress = 0;
                _generationStatus = "Preparing…";
                _generationLastPath = null;
                _generationActive = true;
                _generationCancellation = new CancellationTokenSource();
                EditorApplication.delayCall -= StartNextGenerationOutput;
                EditorApplication.delayCall += StartNextGenerationOutput;
                Repaint();
            }
            catch (Exception exception)
            {
                FinishGeneration(exception);
            }
        }

        private void StartNextGenerationOutput()
        {
            if (!_generationActive) return;
            if (_generationCancellation == null || _generationCancellation.IsCancellationRequested)
            {
                FinishGeneration(cancelled: true);
                return;
            }
            if (_generationOutputs == null || _generationOutputPosition >= _generationOutputs.Length)
            {
                FinishGeneration();
                return;
            }
            try
            {
                int outputIndex = _generationOutputs[_generationOutputPosition];
                TexturePackOutput outputSnapshot = _generationOutputSnapshots[_generationOutputPosition];
                string outputName = outputSnapshot.name;
                _generationStatus = "Reading sources for " + outputName + "…";
                Repaint();
                _generationPlan = TexturePackProcessor.PrepareBake(_generationRecipe, outputIndex,
                    _generationAnchor, _generationSuffix, outputSnapshot, _generationSources);
                int position = _generationOutputPosition;
                int count = _generationOutputs.Length;
                CancellationToken cancellation = _generationCancellation.Token;
                TexturePackBakePlan plan = _generationPlan;
                _generationTask = Task.Run(() => TexturePackProcessor.ExecuteBake(plan, localProgress =>
                {
                    _generationUpdates.Enqueue(new GenerationProgress
                    {
                        value = (position + localProgress) / count,
                        status = (localProgress < .86f ? "Packing " : "Writing ") + outputName + "…"
                    });
                }, cancellation), cancellation);
            }
            catch (Exception exception)
            {
                FinishGeneration(exception);
            }
        }

        private void GenerationUpdate()
        {
            while (_generationUpdates.TryDequeue(out GenerationProgress progress))
            {
                _generationProgress = progress.value;
                _generationStatus = progress.status;
                Repaint();
            }
            if (_generationTask == null || !_generationTask.IsCompleted) return;
            Task completed = _generationTask;
            _generationTask = null;
            try
            {
                completed.GetAwaiter().GetResult();
                if (_generationCancellation == null || _generationCancellation.IsCancellationRequested)
                {
                    FinishGeneration(cancelled: true);
                    return;
                }
                _generationStatus = "Importing " + Path.GetFileName(_generationPlan.OutputPath) + "…";
                _generationProgress = (_generationOutputPosition + .99f) / _generationOutputs.Length;
                Repaint();
                _generationLastPath = TexturePackProcessor.CompleteBake(_generationPlan, _generationRecipe);
                _generationPlan.Dispose();
                _generationPlan = null;
                _generationOutputPosition++;
                if (_generationOutputPosition >= _generationOutputs.Length) FinishGeneration();
                else
                {
                    EditorApplication.delayCall -= StartNextGenerationOutput;
                    EditorApplication.delayCall += StartNextGenerationOutput;
                }
            }
            catch (OperationCanceledException)
            {
                FinishGeneration(cancelled: true);
            }
            catch (Exception exception)
            {
                FinishGeneration(exception);
            }
        }

        private void FinishGeneration(Exception exception = null, bool cancelled = false)
        {
            EditorApplication.delayCall -= StartNextGenerationOutput;
            _generationPlan?.Dispose();
            _generationPlan = null;
            _generationTask = null;
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            int generatedCount = _generationOutputPosition;
            _generationOutputs = null;
            _generationOutputSnapshots = null;
            _generationRecipe = null;
            if (_generationRecipeSnapshot != null) DestroyImmediate(_generationRecipeSnapshot);
            _generationRecipeSnapshot = null;
            _generationSources = null;
            _generationAnchor = null;
            _generationSuffix = null;
            _generationActive = false;
            while (_generationUpdates.TryDequeue(out GenerationProgress ignored)) { }
            if (exception != null)
            {
                Debug.LogException(exception);
                if (EditorUtility.DisplayDialog("Texture Pack Editor", exception.Message, "Copy error", "Close"))
                {
                    EditorGUIUtility.systemCopyBuffer = exception.Message;
                    ShowNotification(new GUIContent("Error copied to clipboard"));
                }
            }
            else if (cancelled) ShowNotification(new GUIContent("Texture generation cancelled"));
            else if (_generationLastPath != null)
            {
                ShowNotification(new GUIContent("Generated " + generatedCount + " texture(s)"));
            }
            RefreshSourceSet(false);
        }

        private void Changed(int channelMask = 15)
        {
            if (recipe != null && recipe != _transientRecipe) EditorUtility.SetDirty(recipe);
            _previewDirty = true;
            _previewDirtyChannels |= (channelMask & 15) | _previewInFlightChannels;
            MarkPendingPreviews(_previewDirtyChannels);
            _previewRevision++;
            _previewCancellation?.Cancel();
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
            double now = EditorApplication.timeSinceStartup;
            if (now - _lastSpinnerRepaint >= .08 && _pendingPreviewNodes.Values.Any(start => now - start >= .2))
            {
                _lastSpinnerRepaint = now;
                Repaint();
            }
            bool installing = TexturePackDeepBump.Installing;
            if (_deepBumpWasInstalling && !installing && TexturePackDeepBump.Ready) Changed();
            _deepBumpWasInstalling = installing;
            GenerationUpdate();
            while (_previewUpdates.TryDequeue(out TexturePackPreviewUpdate update))
                if (update.revision == _previewRevision) ApplyPreviewUpdate(update);

            if (_previewTask != null && _previewTask.IsCompleted)
            {
                try
                {
                    if (_previewTask.IsFaulted)
                    {
                        _previewError = _previewTask.Exception?.GetBaseException().Message;
                        if (!_previewDirty) _pendingPreviewNodes.Clear();
                    }
                }
                finally
                {
                    _previewSession?.Dispose();
                    _previewSession = null;
                    _previewTask = null;
                    _previewCancellation?.Dispose();
                    _previewCancellation = null;
                }
                // Progressive updates already applied each node and the packed output as they became ready.
                Repaint();
            }
            if (_previewTask == null && _previewDirty && EditorApplication.timeSinceStartup >= _previewDue)
                StartPreview();
        }

        private void StartPreview()
        {
            if (!HasInput || recipe == null || recipe.outputs.Count == 0)
            {
                _pendingPreviewNodes.Clear();
                return;
            }
            _previewDirty = false;
            _previewError = null;
            try
            {
                _sourceSet = DetectSources(recipe);
                TexturePackOutput snapshot = ActiveOutput.Clone(true);
                PrunePreviewCaches(snapshot);
                int channelMask = _previewDirtyChannels == 0 ? 15 : _previewDirtyChannels;
                if (_lastOutputPixels == null || _lastOutputPixels.Length != previewResolution * previewResolution)
                    channelMask = 15;
                _previewInFlightChannels = channelMask;
                MarkPendingPreviews(channelMask);
                _previewDirtyChannels = 0;
                _previewSession = new TexturePackPixelSession(_sourceSet, previewResolution, previewResolution,
                    PreviewUvRect(), compact: true);
                _previewSession.Prepare(snapshot.channels.Where((stack, channel) =>
                    (channelMask & (1 << channel)) != 0).SelectMany(stack => stack.nodes));
                int revision = _previewRevision;
                TexturePackPixelSession session = _previewSession;
                Color32[] previousOutput = _lastOutputPixels == null ? null :
                    (Color32[])_lastOutputPixels.Clone();
                int priorityChannel = _lastSelectedChannel >= 0 ? _lastSelectedChannel : _activeChannel;
                string priorityNode = _lastSelectedId;
                _previewCancellation = new CancellationTokenSource();
                CancellationToken cancellation = _previewCancellation.Token;
                _previewTask = Task.Run(() =>
                {
                    TexturePackPreviewWork.Compute(revision, snapshot, session,
                        channelMask, previousOutput, priorityChannel, priorityNode,
                        update => _previewUpdates.Enqueue(update), thumbnailSize: 96,
                        retainNodeResults: false, takeOwnershipPreviousOutput: true,
                        cancellationToken: cancellation);
                }, cancellation);
            }
            catch (Exception exception)
            {
                _previewError = exception.Message;
                _pendingPreviewNodes.Clear();
                _previewDirtyChannels |= _previewInFlightChannels;
                _previewInFlightChannels = 0;
                _previewSession?.Dispose();
                _previewSession = null;
            }
        }

        private void ApplyPreviewUpdate(TexturePackPreviewUpdate update)
        {
            if (update.isOutput)
            {
                _previewInFlightChannels = 0;
                _lastOutputPixels = update.pixels;
                if (!showSelectedNode) RebuildLargePreview();
            }
            else
            {
                _pendingPreviewNodes.Remove(update.nodeId);
                _nodePreviews.TryGetValue(update.nodeId, out Texture2D previous);
                _nodePreviewPixels[update.nodeId] = update.pixels;
                Color32[] thumbnail = DownsampleOpaque(update.pixels, update.width, update.height, 96,
                    out int thumbnailWidth, out int thumbnailHeight);
                _nodePreviews[update.nodeId] = UpdatePreviewTexture(previous, thumbnailWidth, thumbnailHeight, thumbnail);
                if (update.histogram != null)
                {
                    _histograms[update.nodeId] = update.histogram;
                }
                if (showSelectedNode && update.nodeId == _lastSelectedId) RebuildLargePreview();
            }
            Repaint();
        }

        private void PrunePreviewCaches(TexturePackOutput output)
        {
            var valid = new HashSet<string>(output.channels.SelectMany(stack => stack.nodes).Select(node => node.id));
            foreach (string id in _pendingPreviewNodes.Keys.Where(id => !valid.Contains(id)).ToArray())
                _pendingPreviewNodes.Remove(id);
            foreach (string id in _nodePreviews.Keys.Where(id => !valid.Contains(id)).ToArray())
            {
                if (_nodePreviews[id] != null) DestroyImmediate(_nodePreviews[id]);
                _nodePreviews.Remove(id);
            }
            foreach (string id in _nodePreviewPixels.Keys.Where(id => !valid.Contains(id)).ToArray())
                _nodePreviewPixels.Remove(id);
            foreach (string id in _histograms.Keys.Where(id => !valid.Contains(id)).ToArray())
                _histograms.Remove(id);
        }

        private static Texture2D UpdatePreviewTexture(Texture2D texture, int width, int height, Color32[] pixels)
        {
            if (texture == null || texture.width != width || texture.height != height)
            {
                if (texture != null) DestroyImmediate(texture);
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false, true)
                { name = "TexturePackEditor Preview", hideFlags = HideFlags.HideAndDontSave };
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private void DestroyPreviews()
        {
            foreach (Texture2D preview in _nodePreviews.Values) if (preview != null) DestroyImmediate(preview);
            _nodePreviews.Clear();
            _nodePreviewPixels.Clear();
            _histograms.Clear();
            if (_largePreview != null) DestroyImmediate(_largePreview);
            _largePreview = null;
            _lastOutputPixels = null;
        }

        private void ClearPreviewUpdates()
        {
            while (_previewUpdates.TryDequeue(out _)) { }
        }

        private static void ReleaseStaticTextures()
        {
            foreach (Texture2D texture in GradientTextures.Values)
                if (texture != null) DestroyImmediate(texture);
            GradientTextures.Clear();
            foreach (GUIStyle style in NodeHeaderStyles.Values)
                if (style?.normal.background != null) DestroyImmediate(style.normal.background);
            NodeHeaderStyles.Clear();
            foreach (GUIStyle style in CollapsedNodeStyles.Values)
                if (style?.normal.background != null) DestroyImmediate(style.normal.background);
            CollapsedNodeStyles.Clear();
            _nodeContainerStyle = null;
            _nodeBodyStyle = null;
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
            if (histogram != null) DrawHistogram(new Rect(rect.x + 5, rect.y, rect.width - 10, rect.height - 20), histogram);
            Rect track = new(rect.x + 5, rect.yMax - 17, rect.width - 10, 9);
            DrawGradient(track, left, right);
            float[] values = { minimum, maximum };
            if (HandleMultiSlider(key, track, values) >= 0)
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
            DrawHistogram(histogramRect, histogram);
            Rect track = new(rect.x + 5, rect.yMax - 18, rect.width - 10, 9);
            DrawGradient(track, Color.black, Color.white);
            float midpoint = Mathf.Lerp(node.inputBlack, node.inputWhite, Mathf.Pow(.5f, node.gamma));
            float midpointProportion = Mathf.InverseLerp(node.inputBlack, node.inputWhite, midpoint);
            float[] values = { node.inputBlack, midpoint, node.inputWhite };
            int changedHandle = HandleMultiSlider(node.id + ":levelsInput", track, values, true);
            if (changedHandle >= 0)
            {
                node.inputBlack = values[0];
                node.inputWhite = values[2];
                if (changedHandle == 1)
                {
                    float range = node.inputWhite - node.inputBlack;
                    if (range > .0001f)
                    {
                        float normalized = Mathf.Clamp((values[1] - node.inputBlack) / range, .0625f, .9659f);
                        node.gamma = Mathf.Clamp(Mathf.Log(normalized) / Mathf.Log(.5f), .05f, 4);
                    }
                }
                midpoint = changedHandle == 1 ? values[1] :
                    Mathf.Lerp(node.inputBlack, node.inputWhite, midpointProportion);
            }
            DrawThumb(track, node.inputBlack, Color.black);
            DrawThumb(track, midpoint, Color.gray);
            DrawThumb(track, node.inputWhite, Color.white);
        }

        private int HandleMultiSlider(string key, Rect track, float[] values, bool endpointsSpanMiddle = false)
        {
            int changedHandle = -1;
            Event current = Event.current;
            bool begin = current.type == EventType.MouseDown &&
                         new Rect(track.x, track.y - 8, track.width, track.height + 20).Contains(current.mousePosition);
            if (begin)
            {
                float normalized = Mathf.InverseLerp(track.x, track.xMax, current.mousePosition.x);
                _sliderHandle = 0;
                for (int i = 1; i < values.Length; i++)
                    if (Mathf.Abs(values[i] - normalized) < Mathf.Abs(values[_sliderHandle] - normalized)) _sliderHandle = i;
                _sliderKey = key;
            }
            if ((begin || current.type == EventType.MouseDrag) && _sliderKey == key && _sliderHandle >= 0)
            {
                float value = Mathf.Clamp01(Mathf.InverseLerp(track.x, track.xMax, current.mousePosition.x));
                bool spanningEndpoint = endpointsSpanMiddle && values.Length == 3 && _sliderHandle != 1;
                float low = _sliderHandle == 0 ? 0 : spanningEndpoint ? values[0] : values[_sliderHandle - 1];
                float high = _sliderHandle == values.Length - 1 ? 1 : spanningEndpoint ? values[2] : values[_sliderHandle + 1];
                values[_sliderHandle] = Mathf.Clamp(value, low, high);
                changedHandle = _sliderHandle;
                GUI.changed = true;
                current.Use();
            }
            if (current.type == EventType.MouseUp && _sliderKey == key)
            {
                _sliderKey = null; _sliderHandle = -1; current.Use();
            }
            return changedHandle;
        }

        private static void DrawHistogram(Rect rect, float[] histogram)
        {
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, .28f));
            if (histogram == null || histogram.Length < 2) return;
            Vector3[] points = histogram.Length == HistogramPoints.Length ? HistogramPoints :
                new Vector3[histogram.Length];
            for (int index = 0; index < histogram.Length; index++)
                points[index] = new Vector3(Mathf.Lerp(rect.x, rect.xMax, index / (histogram.Length - 1f)),
                    Mathf.Lerp(rect.yMax - 1, rect.y + 1, histogram[index]));
            Handles.BeginGUI();
            Handles.color = new Color(.88f, .88f, .88f, .9f);
            Handles.DrawAAPolyLine(1.75f, points);
            Handles.EndGUI();
        }

        private static void DrawGradient(Rect rect, Color left, Color right)
        {
            GUI.DrawTexture(rect, GradientTexture(left, right), ScaleMode.StretchToFill, true);
        }

        private static Texture2D GradientTexture(Color left, Color right)
        {
            Color32 left32 = left;
            Color32 right32 = right;
            string key = $"{left32.r:X2}{left32.g:X2}{left32.b:X2}{left32.a:X2}-" +
                         $"{right32.r:X2}{right32.g:X2}{right32.b:X2}{right32.a:X2}";
            if (GradientTextures.TryGetValue(key, out Texture2D cached) && cached != null) return cached;
            const int width = 256;
            var pixels = new Color32[width];
            for (int x = 0; x < width; x++) pixels[x] = Color.Lerp(left, right, x / (width - 1f));
            var texture = new Texture2D(width, 1, TextureFormat.RGBA32, false, true)
            { name = "TexturePackEditor Gradient", hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            GradientTextures[key] = texture;
            return texture;
        }

        private static GUIStyle NodeContainerStyle => _nodeContainerStyle ??= new GUIStyle(EditorStyles.helpBox)
        {
            padding = new RectOffset(1, 1, 1, 1),
            margin = new RectOffset(1, 1, 1, 2)
        };

        private static GUIStyle NodeBodyStyle => _nodeBodyStyle ??= new GUIStyle
        {
            padding = new RectOffset(7, 7, 5, 7)
        };

        private static GUIStyle NodeHeaderStyle(TexturePackNodeType type, bool selected)
        {
            int key = (int)type + (selected ? 100 : 0);
            if (NodeHeaderStyles.TryGetValue(key, out GUIStyle style)) return style;
            Color color = WithAlpha(NodeColor(type), selected ? .48f : .30f);
            var texture = CreateRoundedTexture(color, 4, true);
            style = new GUIStyle
            {
                normal = { background = texture },
                border = new RectOffset(5, 5, 5, 1),
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0)
            };
            NodeHeaderStyles[key] = style;
            return style;
        }

        private static GUIStyle CollapsedNodeStyle(TexturePackNodeType type)
        {
            int key = (int)type;
            if (CollapsedNodeStyles.TryGetValue(key, out GUIStyle style)) return style;
            var texture = CreateRoundedTexture(WithAlpha(NodeColor(type), .42f), 4, false);
            style = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { background = texture },
                border = new RectOffset(5, 5, 5, 5),
                padding = new RectOffset(6, 6, 0, 0),
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip
            };
            CollapsedNodeStyles[key] = style;
            return style;
        }

        private void MarkPendingPreviews(int channelMask)
        {
            if (recipe == null || recipe.outputs.Count == 0) return;
            double now = EditorApplication.timeSinceStartup;
            foreach (var node in ActiveOutput.channels.Where((channel, index) => (channelMask & (1 << index)) != 0)
                         .SelectMany(channel => channel.nodes))
                if (!_pendingPreviewNodes.ContainsKey(node.id)) _pendingPreviewNodes.Add(node.id, now);
        }

        private bool NodePreviewBusy(string nodeId, double now)
            => _pendingPreviewNodes.TryGetValue(nodeId, out double started) && now - started >= .2;

        private static void DrawPreviewSpinner(Rect rect)
        {
            int frame = (int)(EditorApplication.timeSinceStartup * 12) % 12;
            var icon = EditorGUIUtility.IconContent("WaitSpin" + frame.ToString("00"));
            GUI.Label(rect, new GUIContent(icon.image, "Updating preview…"));
        }

        private static Texture2D CreateRoundedTexture(Color color, int radius, bool squareBottom)
        {
            const int size = 14;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float nearestX = Mathf.Clamp(x + .5f, radius, size - radius);
                float nearestY = squareBottom
                    ? Mathf.Min(y + .5f, size - radius)
                    : Mathf.Clamp(y + .5f, radius, size - radius);
                float distance = Vector2.Distance(new Vector2(x + .5f, y + .5f), new Vector2(nearestX, nearestY));
                float coverage = Mathf.Clamp01(radius + .5f - distance);
                Color pixel = color;
                pixel.a *= coverage;
                pixels[y * size + x] = pixel;
            }
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
            { name = "TexturePackEditor Chrome", hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static int SignalAfter(TexturePackNode node, int input)
        {
            if (node.type == TexturePackNodeType.Sample)
            {
                int mask = node.channelMask & 15;
                int source = node.sampleDesaturate && node.desaturateAmount >= .999f ||
                    mask != 0 && (mask & (mask - 1)) == 0 ? ScalarSignal : mask;
                return BlendSignal(node, input, source);
            }
            if (node.type == TexturePackNodeType.Constant || node.type == TexturePackNodeType.Height)
                return BlendSignal(node, input, ScalarSignal);
            if (node.type == TexturePackNodeType.Desaturate &&
                (input == ScalarSignal || node.desaturateAmount >= .999f)) return ScalarSignal;
            return input;
        }

        private static int BlendSignal(TexturePackNode node, int input, int source)
        {
            if (node.blendAmount <= 0) return input;
            if (node.blendMode == TexturePackBlendMode.Replace && node.blendAmount >= 1) return source;
            return (input == 0 || input == ScalarSignal) && source == ScalarSignal ? ScalarSignal : 15;
        }

        private static void DrawSignalWires(Rect rect, int signal)
        {
            if (signal == 0) return;
            Handles.BeginGUI();
            foreach ((float x, Color color) in SignalPositions(rect.center.x, signal))
            {
                Handles.color = color;
                Handles.DrawAAPolyLine(4f, new Vector3(x, rect.y), new Vector3(x, rect.yMax));
            }
            Handles.EndGUI();
        }

        private static IEnumerable<(float x, Color color)> SignalPositions(float center, int signal)
        {
            if (signal == ScalarSignal)
            {
                yield return (center, new Color(1, 1, 1, .88f));
                yield break;
            }
            Color[] colors = { Color.red, Color.green, new Color(.2f, .5f, 1f), Color.white };
            int count = CountBits(signal & 15);
            float x = center - (count - 1) * 3f;
            for (int channel = 0; channel < 4; channel++)
                if ((signal & (1 << channel)) != 0)
                {
                    yield return (x, WithAlpha(colors[channel], .88f));
                    x += 6f;
                }
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

        private string NodeTitle(TexturePackNode node)
        {
            if (node.type == TexturePackNodeType.Height) return "Height · " + SampleSourceName(node);
            if (node.type != TexturePackNodeType.Sample)
                return node.type == TexturePackNodeType.MultiplyAdd ? "Multiply + Add" : node.type.ToString();
            string source = node.sourceKind == TexturePackSourceKind.DetectedRole
                ? TexturePackProjectSettings.instance.DisplayName(recipe.EffectiveRole(node)) :
                node.manualTexture == null ? "Missing" : node.manualTexture.name;
            return "Sample " + source + "." + MaskName(node.channelMask) + (node.sampleDesaturate ? " → Gray" : "");
        }

        private static string NodeTooltip(TexturePackNodeType type) => type switch
        {
            TexturePackNodeType.Height => "Reconstructs scalar height from its own full RGB source, independently of incoming wires.",
            TexturePackNodeType.Sample => "Reads the enabled components from a detected or manually assigned texture.",
            TexturePackNodeType.Desaturate => "Uses Rec.709 luminance by default. Amount 1 produces one scalar; partial amounts keep separate channels.",
            TexturePackNodeType.Levels => "Remaps input black, midpoint, white, and output range.",
            TexturePackNodeType.Noise => "Applies deterministic procedural three-octave noise.",
            TexturePackNodeType.Invert => "Replaces each value with one minus that value.",
            TexturePackNodeType.MultiplyAdd => "Scales and offsets the current value.",
            TexturePackNodeType.Constant => "Replaces the current signal with one constant scalar.",
            _ => string.Empty
        };

        private string CollapsedNodeLabel(TexturePackNode node) => node.type switch
        {
            TexturePackNodeType.Sample => "Sample:" + SampleSourceName(node) + "." +
                                          MaskName(node.channelMask).ToLowerInvariant(),
            TexturePackNodeType.Desaturate => "Desat", TexturePackNodeType.Levels => "Levels",
            TexturePackNodeType.MultiplyAdd => "× +", _ => node.type.ToString()
        };

        private string SampleSourceName(TexturePackNode node)
        {
            Texture2D texture = _sourceSet?.Resolve(node);
            if (texture != null) return texture.name;
            return node.sourceKind == TexturePackSourceKind.DetectedRole
                ? TexturePackProjectSettings.instance.DisplayName(recipe.EffectiveRole(node)) : "?";
        }

        private static Color NodeColor(TexturePackNodeType type) => type switch
        {
            TexturePackNodeType.Height => new Color(.65f, .45f, .2f, .3f),
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
