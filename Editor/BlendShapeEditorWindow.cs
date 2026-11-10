// LGC BlendShape Editor v1.6.1. Editor-only blendshape mesh editing.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LGC.BlendShapeTools
{
    public sealed class BlendShapeEditorWindow : EditorWindow
    {
        private const string ToolFolder = "Assets/LGC/For_Avatars/Tools/形态键编辑器";
        [SerializeField] private SkinnedMeshRenderer target;
        [SerializeField] private Mesh source, original;
        [SerializeField] private Mesh sessionOutput;
        [SerializeField] private bool sparseOutput;
        [SerializeField] private GameObject fbxAsset;
        [SerializeField] private Mesh selectedFbxMesh;
        [NonSerialized] private Mesh[] fbxMeshes = new Mesh[0];
        [NonSerialized] private bool fbxArmed;
        [NonSerialized] private double fbxDeadline;
        internal static BlendShapeEditorWindow ActiveWindow { get; private set; }
        internal static event Action PreviewStateChanged;
        internal SkinnedMeshRenderer TargetRenderer => target;
        internal bool PreviewUnlocked => preview != null && Current;
        [SerializeField] private Mesh originalSnapshot;
        [SerializeField] private string originalMeshName;
        [NonSerialized] private bool restoreArmed, applyInProgress;
        [NonSerialized] private double restoreDeadline, applyReadyAt;
        [NonSerialized] private int lastCooldownSeconds;
        [SerializeField] private List<float> originalWeights = new List<float>();
        [SerializeField] private Bounds originalBounds;
        [SerializeField] private List<ShapeDraft> shapes = new List<ShapeDraft>();
        [SerializeField] private string search = "", newName = "NewShape", outputFolder = ToolFolder + "/Meshes";
        private const int ShapesPerGroup = 20;
        [SerializeField] private float batchScale = 1;
        [SerializeField] private List<bool> groupExpanded = new List<bool>();
        [SerializeField] private bool showDeleted = true;
        [SerializeField] private bool listDefaultsApplied;
        [NonSerialized] private bool clearArmed;
        [NonSerialized] private double clearDeadline;
        [NonSerialized] private Mesh preview;
        [NonSerialized] private float[] previewOriginalWeights;
        [NonSerialized] private Bounds previewOriginalBounds;
        private Vector2 scroll, operationsScroll;
        [NonSerialized] private string activePreviewShape, pendingRevealName;
        [NonSerialized] private Rect revealRowRect;
        internal string ActivePreviewShape => activePreviewShape;
        private GUIStyle sectionStyle;
        private string status;
        private MessageType statusType = MessageType.Info;

        [MenuItem("LGC/形态键编辑器", false, 30)]
        public static void Open()
        {
            var window = GetWindow<BlendShapeEditorWindow>();
            window.minSize = new Vector2(1000, 480);
            window.Show();
        }

        // v1.6.1: resolve documentation from the installed script, including VPM/UPM package locations.
        private void OpenReadme()
        {
            string scriptPath = AssetDatabase.GetAssetPath(MonoScript.FromScriptableObject(this));
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(scriptPath);
            string folder = package != null ? package.resolvedPath : Path.GetDirectoryName(Path.GetDirectoryName(scriptPath));
            string readme = Path.GetFullPath(Path.Combine(folder ?? ToolFolder, "README.md"));
            if (File.Exists(readme)) EditorUtility.OpenWithDefaultApp(readme);
            else SetStatus("未找到说明文件：" + readme, MessageType.Warning);
        }

        private void OnEnable()
        {
            ActiveWindow = this;
            CancelConfirmations();
            if (fbxAsset != null) RefreshFbxMeshes();
            if (!listDefaultsApplied) { showDeleted = true; listDefaultsApplied = true; }
            titleContent = new GUIContent("形态键编辑器 v" + BlendShapeMeshEditor.Version);
            minSize = new Vector2(1000, 480);
            if (string.IsNullOrEmpty(originalMeshName) && original != null) originalMeshName = original.name;
            if (original != null && originalSnapshot == null) CaptureOriginalSnapshot();
            AssemblyReloadEvents.beforeAssemblyReload += StopPreview;
            EditorApplication.playModeStateChanged += OnPlayMode;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            Undo.undoRedoPerformed += OnUndo;
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable()
        {
            StopPreview();
            AssemblyReloadEvents.beforeAssemblyReload -= StopPreview;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            Undo.undoRedoPerformed -= OnUndo;
            EditorApplication.update -= OnEditorUpdate;
            if (ActiveWindow == this) ActiveWindow = null;
            PreviewStateChanged?.Invoke();
        }

        private void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            bool repaint = false;
            if (clearArmed && now >= clearDeadline) { clearArmed = false; repaint = true; }
            if (restoreArmed && now >= restoreDeadline) { restoreArmed = false; repaint = true; }
            if (fbxArmed && now >= fbxDeadline) { fbxArmed = false; repaint = true; }
            int seconds = ApplyCooldownSeconds;
            if (seconds != lastCooldownSeconds) { lastCooldownSeconds = seconds; repaint = true; }
            if (repaint) Repaint();
        }

        private int ApplyCooldownSeconds => Math.Max(0, (int)Math.Ceiling(applyReadyAt - EditorApplication.timeSinceStartup));
        private bool CanEdit => Current && ValidTarget(target) && source.isReadable;
        private bool CanRestoreOriginal => ValidTarget(target) && (original != null || originalSnapshot != null);

        private void CancelConfirmations() { clearArmed = false; restoreArmed = false; fbxArmed = false; }

        private void OnDestroy()
        {
            // Closing ends the session; OnDisable also runs during script reload and must retain it.
            StopPreview();
            ReleaseOriginalSnapshot();
            target = null;
            source = original = sessionOutput = null;
            fbxAsset = null; selectedFbxMesh = null; fbxMeshes = new Mesh[0]; sparseOutput = false;
            originalMeshName = null;
            originalWeights.Clear();
            originalBounds = new Bounds();
            shapes.Clear();
            groupExpanded.Clear();
            search = "";
            newName = "NewShape";
            batchScale = 1;
            scroll = operationsScroll = Vector2.zero;
            clearDeadline = restoreDeadline = applyReadyAt = 0;
            applyInProgress = false;
            lastCooldownSeconds = 0;
            status = null;
            activePreviewShape = pendingRevealName = null;
        }

        private void ReleaseOriginalSnapshot()
        {
            if (originalSnapshot != null) Object.DestroyImmediate(originalSnapshot);
            originalSnapshot = null;
        }

        private void CaptureOriginalSnapshot()
        {
            if (original == null || originalSnapshot != null) return;
            originalSnapshot = Object.Instantiate(original);
            originalSnapshot.name = originalMeshName;
            originalSnapshot.hideFlags = HideFlags.HideAndDontSave;
        }

        private void ReloadCurrentMesh()
        {
            StopPreview();
            source = target != null ? target.sharedMesh : null;
            ReadShapes();
            SetStatus("已重新读取当前网格；拖入时记录的原始网格与快照保持不变。", MessageType.Info);
        }

        private void OnPlayMode(PlayModeStateChange state) { if (state == PlayModeStateChange.ExitingEditMode) StopPreview(); }
        private void OnSceneSaving(Scene scene, string path) { StopPreview(); }
        private void OnUndo()
        {
            StopPreview();
            if (target != null)
            { source = target.sharedMesh; ReadShapes(); status = "已跟随 Undo / Redo 重新读取网格，未应用的编辑已清空。"; }
            Repaint();
        }

        private static bool ValidTarget(SkinnedMeshRenderer renderer) => renderer != null &&
            !EditorUtility.IsPersistent(renderer) && renderer.gameObject.scene.IsValid() &&
            !EditorSceneManager.IsPreviewScene(renderer.gameObject.scene);

        private void Bind(SkinnedMeshRenderer renderer)
        {
            BlendShapePreviewWindow.BeforeMainBind(renderer);
            StopPreview();
            ReleaseOriginalSnapshot();
            target = renderer;
            sessionOutput = null;
            fbxAsset = null; selectedFbxMesh = null; fbxMeshes = new Mesh[0];
            original = source = target != null ? target.sharedMesh : null;
            originalWeights = target != null && source != null ? Weights(target, source).ToList() : new List<float>();
            originalBounds = target != null ? target.localBounds : new Bounds();
            originalMeshName = original != null ? original.name : "Mesh";
            ReadShapes(); status = null;
            try { CaptureOriginalSnapshot(); }
            catch (Exception error) { SetStatus("初始网格快照创建失败，仍可使用原网格资源恢复：" + error.Message, MessageType.Warning); }
            PreviewStateChanged?.Invoke();
        }

        private void ReadShapes()
        {
            CancelConfirmations();
            shapes = new List<ShapeDraft>();
            groupExpanded = new List<bool>();
            scroll = Vector2.zero;
            activePreviewShape = pendingRevealName = null;
            if (source == null) return;
            for (int i = 0; i < source.blendShapeCount; i++)
                shapes.Add(new ShapeDraft { sourceIndex = i, name = source.GetBlendShapeName(i) });
        }

        private static float[] Weights(SkinnedMeshRenderer renderer, Mesh mesh)
        {
            var weights = new float[mesh.blendShapeCount];
            for (int i = 0; i < weights.Length; i++) weights[i] = renderer.GetBlendShapeWeight(i);
            return weights;
        }

        private bool Current => target != null && source != null &&
            target.sharedMesh == (preview != null ? preview : source);

        private void OnGUI()
        {
            float previous = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 82;
            // Anchor the root layout to the window so nested scroll views receive its remaining height.
            GUILayout.BeginArea(new Rect(6, 6, Mathf.Max(1, position.width - 12), Mathf.Max(1, position.height - 12)));
            try
            {
                using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
                {
                    var next = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("目标网格", target, typeof(SkinnedMeshRenderer), true);
                    if (next != target)
                    {
                        if (next == null || ValidTarget(next)) Bind(next);
                        else SetStatus("请选择普通场景中的 SkinnedMeshRenderer，暂不直接编辑资源或预制件编辑模式。", MessageType.Warning);
                    }
                    if (target != null)
                    {
                        if (source == null || target.sharedMesh == null)
                            EditorGUILayout.HelpBox(CanRestoreOriginal
                                ? "当前网格丢失或已被删除。可在右侧点击“恢复到原始网格-未修改”，恢复拖入时记录的网格。"
                                : "当前没有可用网格，也没有本次会话的初始备份，请指定有效网格。", MessageType.Warning);
                        else if (!Current)
                        {
                            EditorGUILayout.HelpBox("目标网格已被其他操作替换，可重新读取，或使用右侧原始网格恢复。", MessageType.Warning);
                            if (GUILayout.Button("重新读取当前目标（清空编辑，保留原始恢复基准）")) ReloadCurrentMesh();
                        }
                        using (new EditorGUI.DisabledScope(true)) EditorGUILayout.ObjectField("编辑源网格", source, typeof(Mesh), false);
                        if (source != null && !source.isReadable)
                            EditorGUILayout.HelpBox("请在模型导入设置中开启 Read/Write 后再使用。原始网格恢复仍可使用。", MessageType.Warning);
                        DrawEditor();
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("拖入人物 Body 或其他 SkinnedMeshRenderer。所有编辑先暂存，应用时才生成独立 Mesh。", MessageType.Info);
                        if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, statusType);
                        GUILayout.FlexibleSpace();
                    }
                }
                DrawFooter();
            }
            finally
            {
                GUILayout.EndArea();
                EditorGUIUtility.labelWidth = previous;
            }
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("打开说明", GUILayout.Width(90)))
                    OpenReadme();
                GUILayout.FlexibleSpace();
                GUILayout.Label("v" + BlendShapeMeshEditor.Version, EditorStyles.miniLabel);
            }
        }

        private void DrawEditor()
        {
            const float gap = 12;
            float columnsWidth = Mathf.Max(1, position.width - 12 - gap);
            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(columnsWidth * 0.60f), GUILayout.ExpandHeight(true)))
                using (new EditorGUI.DisabledScope(!CanEdit))
                    DrawShapeList();
                GUILayout.Space(gap);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox,
                    GUILayout.Width(columnsWidth * 0.40f), GUILayout.ExpandHeight(true)))
                {
                    operationsScroll = EditorGUILayout.BeginScrollView(operationsScroll, GUILayout.ExpandHeight(true));
                    DrawOperations();
                    EditorGUILayout.EndScrollView();
                }
            }
        }

        private void DrawShapeList()
        {
            EnsureGroupStates();
            EditorGUI.BeginChangeCheck();
            search = EditorGUILayout.TextField("搜索", search);
            using (new EditorGUILayout.HorizontalScope())
            {
                showDeleted = GUILayout.Toggle(showDeleted, "显示待删除", GUILayout.Width(100));
                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(search)))
                {
                    if (GUILayout.Button("全部展开", GUILayout.Width(76))) SetAllGroupsExpanded(true);
                    if (GUILayout.Button("全部折叠", GUILayout.Width(76))) SetAllGroupsExpanded(false);
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("全选", GUILayout.Width(64)))
                { CancelConfirmations(); foreach (var shape in Visible()) shape.selected = true; }
                if (GUILayout.Button("取消选择", GUILayout.Width(80)))
                { CancelConfirmations(); foreach (var shape in shapes) shape.selected = false; }
            }
            if (EditorGUI.EndChangeCheck()) CancelConfirmations();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("选", GUILayout.Width(24));
                GUILayout.Label("形态键名称", GUILayout.MinWidth(140));
                GUILayout.Label(new GUIContent("形态键上限", "以修改前的 Unity 权重 100 为基准。62 = 原来 62 的效果，180 = 原来 180 的效果。"), GUILayout.Width(80));
                GUILayout.Label("混合权重 %", GUILayout.Width(80));
                GUILayout.Label(preview == null ? "预览（已锁定）" : "预览 0～100", GUILayout.Width(170));
            }
            if (Event.current.type == EventType.Repaint) revealRowRect = Rect.zero;
            // Do not set MinHeight here: GUIScrollGroup would treat it as a height cap and stop stretching.
            scroll = EditorGUILayout.BeginScrollView(scroll, EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            bool anyVisible = false;
            bool searching = !string.IsNullOrEmpty(search);
            for (int group = 0; group < groupExpanded.Count; group++)
            {
                int start = group * ShapesPerGroup;
                int count = Math.Min(ShapesPerGroup, shapes.Count - start);
                var matches = shapes.GetRange(start, count).Where(IsVisible).ToArray();
                if (matches.Length == 0) continue;
                anyVisible = true;
                using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                {
                    bool expanded = searching || groupExpanded[group];
                    using (new EditorGUI.DisabledScope(searching))
                    {
                        EditorGUI.BeginChangeCheck();
                        expanded = EditorGUILayout.Foldout(expanded,
                            $"第 {group + 1} 组 · {start + 1}–{start + count} · {matches.Length} 项", true);
                        if (EditorGUI.EndChangeCheck()) { CancelConfirmations(); groupExpanded[group] = expanded; }
                    }
                }
                if (searching || groupExpanded[group])
                    foreach (var shape in matches) DrawRow(shape);
            }
            if (!anyVisible) GUILayout.Label(shapes.Count == 0 ? "当前没有形态键，可在右侧新建。" : "没有符合筛选条件的形态键。", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();
            if (Event.current.type == EventType.Repaint && pendingRevealName != null && revealRowRect.height > 0)
            {
                scroll = RevealRow(scroll, revealRowRect, GUILayoutUtility.GetLastRect().height);
                pendingRevealName = null; Repaint();
            }
            GUILayout.Label($"保留 {shapes.Count(s => !s.deleted)} 项 · 待删除 {shapes.Count(s => s.deleted)} 项 · 已选 {shapes.Count(s => s.selected && !s.deleted)} 项", EditorStyles.miniLabel);
        }

        private void EnsureGroupStates()
        {
            if (groupExpanded == null) groupExpanded = new List<bool>();
            int count = (shapes.Count + ShapesPerGroup - 1) / ShapesPerGroup;
            while (groupExpanded.Count < count) groupExpanded.Add(groupExpanded.Count == 0);
            if (groupExpanded.Count > count) groupExpanded.RemoveRange(count, groupExpanded.Count - count);
        }

        private void SetAllGroupsExpanded(bool expanded)
        {
            CancelConfirmations();
            EnsureGroupStates();
            for (int i = 0; i < groupExpanded.Count; i++) groupExpanded[i] = expanded;
        }

        private EditorGUILayout.VerticalScope BeginSection(string title)
        {
            if (sectionStyle == null)
                sectionStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(12, 12, 10, 12),
                    margin = new RectOffset(0, 0, 0, 18)
                };
            var scope = new EditorGUILayout.VerticalScope(sectionStyle);
            GUILayout.Label(title, EditorStyles.boldLabel);
            GUILayout.Space(6);
            return scope;
        }

        private void DrawOperations()
        {
            using (BeginSection("预览"))
            {
                using (new EditorGUI.DisabledScope(!CanEdit))
                    if (GUILayout.Button(preview == null ? "解锁预览" : "锁定预览", GUILayout.Height(30)))
                        Run(() => { if (preview != null) StopPreview(); else StartPreview(); });
                if (GUILayout.Button("预览模型2", GUILayout.Height(28))) BlendShapePreviewWindow.Open();
                GUILayout.Label(preview == null
                    ? "滑条已锁定。点击解锁预览后加载临时网格，再拖动左侧滑条。首次加载可能需要稍候。"
                    : "滑条已解锁。锁定预览会恢复原网格和权重；修改形态键后需要重新解锁。", EditorStyles.wordWrappedMiniLabel);
            }
            using (BeginSection("所选形态键"))
            using (new EditorGUI.DisabledScope(!CanEdit))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!shapes.Any(s => s.selected && !s.deleted)))
                        if (GUILayout.Button("删除所选"))
                        { StopPreview(); foreach (var shape in shapes.Where(s => s.selected)) { shape.deleted = true; shape.previewWeight = 0; } }
                    using (new EditorGUI.DisabledScope(!shapes.Any(s => s.selected && s.deleted)))
                        if (GUILayout.Button("恢复所选"))
                        { StopPreview(); foreach (var shape in shapes.Where(s => s.selected)) shape.deleted = false; }
                }
                EditorGUI.BeginChangeCheck();
                float limit = EditorGUILayout.FloatField(new GUIContent("批量上限", "100 保持原效果；62 为原来的 62；180 为原来的 180。"), batchScale * 100f);
                if (EditorGUI.EndChangeCheck())
                {
                    CancelConfirmations();
                    batchScale = BlendShapeMeshEditor.Finite(limit) ? Mathf.Clamp(limit, -1000, 1000) / 100f : limit;
                }
                using (new EditorGUI.DisabledScope(!shapes.Any(s => s.selected && !s.deleted)))
                    if (GUILayout.Button("设为此形态键上限"))
                    {
                        CancelConfirmations();
                        if (!BlendShapeMeshEditor.Finite(batchScale)) SetStatus("形态键上限必须是有效数字。", MessageType.Warning);
                        else { StopPreview(); foreach (var shape in shapes.Where(s => s.selected && !s.deleted)) shape.scale = Mathf.Clamp(batchScale, -10, 10); }
                    }
                GUILayout.Label("形态键上限 100 为基准：例如填 62，生成后拉到 100 就是到 62 的效果；填 180 就是突破上限到 180 的效果。范围 −1000～1000，负数将反向执行顶点。", EditorStyles.wordWrappedMiniLabel);
            }
            using (BeginSection("新建与混合"))
            using (new EditorGUI.DisabledScope(!CanEdit))
            {
                EditorGUI.BeginChangeCheck();
                newName = EditorGUILayout.TextField("新形态键名", newName);
                if (EditorGUI.EndChangeCheck()) CancelConfirmations();
                if (GUILayout.Button("新建空形态键")) Run(() => Add(new ShapeDraft { name = newName }));
                using (new EditorGUI.DisabledScope(!shapes.Any(s => s.selected && !s.deleted)))
                    if (GUILayout.Button("混合所选为新形态键")) Run(() =>
                    {
                        Add(BlendShapeMeshEditor.Mix(shapes.Where(s => s.selected && !s.deleted).ToArray(), newName));
                        SetStatus("已添加混合结果；原形态键保留。按所选行的混合权重和形态键上限生成，结果在预览 100 时完全呈现。", MessageType.Info);
                    });
                GUILayout.Label("左侧混合权重仅用于创建混合结果。", EditorStyles.wordWrappedMiniLabel);
            }
            using (BeginSection("网格保存与恢复"))
            {
                GUILayout.Label("输出文件夹", EditorStyles.miniBoldLabel);
                EditorGUILayout.SelectableLabel(outputFolder, EditorStyles.textField,
                    GUILayout.Height(EditorGUIUtility.singleLineHeight));
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(HasSessionOutput))
                        if (GUILayout.Button("选择文件夹", GUILayout.Height(25))) Run(SelectOutputFolder);
                    if (GUILayout.Button("定位输出文件夹", GUILayout.Height(25))) Run(LocateOutputFolder);
                }
                if (HasSessionOutput) GUILayout.Label("本次将覆盖：" + AssetDatabase.GetAssetPath(sessionOutput) + "。输出位置在本次目标会话内固定。", EditorStyles.wordWrappedMiniLabel);
                GUILayout.Space(8);
                int cooldown = ApplyCooldownSeconds;
                using (new EditorGUI.DisabledScope(!CanEdit || applyInProgress || cooldown > 0))
                    if (GUILayout.Button(applyInProgress ? "正在保存…" : cooldown > 0
                        ? $"应用并保存新网格（冷却 {cooldown} 秒）" : "应用并保存新网格", GUILayout.Height(30))) Run(Apply);
                GUILayout.Space(6);
                DrawRestoreOriginalButton();
                EditorGUI.BeginChangeCheck();
                sparseOutput = EditorGUILayout.ToggleLeft("稀疏生成", sparseOutput);
                if (EditorGUI.EndChangeCheck()) StopPreview();
                if (sparseOutput) EditorGUILayout.HelpBox("稀疏生成仅保留形态键顶点位移，可减小网格文件体积，但会移除法线和切线变化，可能造成光照、阴影、高光或法线贴图效果缺失。", MessageType.Warning);
                GUILayout.Label("原始网格是拖入时记录的网格、权重和包围盒；原资源被删除时可从本次会话快照另存后恢复。", EditorStyles.wordWrappedMiniLabel);
            }
            using (BeginSection("默认参数"))
            using (new EditorGUI.DisabledScope(!CanEdit))
            {
                Color oldColor = GUI.backgroundColor;
                if (clearArmed) GUI.backgroundColor = new Color(1f, 0.3f, 0.3f);
                bool clearClicked = GUILayout.Button(clearArmed ? "再次点击将清除全部待应用的操作" : "恢复默认参数", GUILayout.Height(30));
                GUI.backgroundColor = oldColor;
                if (clearClicked) RequestClearEdits();
                GUILayout.Label(clearArmed ? "请在 5 秒内再次点击确认，超时自动取消。" : "清除未应用的编辑草稿，需要点击两次确认；不撤销已保存的网格修改。", EditorStyles.wordWrappedMiniLabel);
            }
            DrawFbxSection();
            GUILayout.Label("删除或改名后，已有动画、面捕和其他组件中的同名引用需自行调整。", EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, statusType);
        }

        private void DrawRestoreOriginalButton()
        {
            using (new EditorGUI.DisabledScope(!CanRestoreOriginal))
            {
                Color color = GUI.backgroundColor;
                if (restoreArmed) GUI.backgroundColor = new Color(1f, 0.3f, 0.3f);
                string label = restoreArmed ? "再次点击恢复原始网格" : "恢复到原始网格-未修改";
                bool clicked = GUILayout.Button(label, GUILayout.Height(30));
                GUI.backgroundColor = color;
                if (clicked) Run(RequestRestoreOriginal);
            }
            GUILayout.Label(restoreArmed
                ? "请在 5 秒内再次点击确认。此操作不会删除已生成的文件。"
                : "此操作不会删除已生成的文件。", EditorStyles.wordWrappedMiniLabel);
        }

        private void RequestRestoreOriginal()
        {
            if (!CanRestoreOriginal) throw new InvalidOperationException("本次会话没有可恢复的初始网格或快照。");
            double now = EditorApplication.timeSinceStartup;
            if (!restoreArmed || now >= restoreDeadline)
            { CancelConfirmations(); restoreArmed = true; restoreDeadline = now + 5; Repaint(); return; }
            RestoreOriginal();
        }

        private void DrawFbxSection()
        {
            using (BeginSection("FBX 备用恢复"))
            {
                EditorGUI.BeginChangeCheck();
                var asset = (GameObject)EditorGUILayout.ObjectField("原始 FBX", fbxAsset, typeof(GameObject), false);
                if (EditorGUI.EndChangeCheck())
                {
                    CancelConfirmations(); fbxAsset = asset;
                    Run(RefreshFbxMeshes);
                }
                using (new EditorGUI.DisabledScope(!ValidTarget(target)))
                    if (GUILayout.Button("自动查找 FBX", GUILayout.Height(25))) Run(FindFbx);
                if (fbxMeshes != null && fbxMeshes.Length > 0)
                {
                    var labels = new[] { "请选择要恢复的网格" }.Concat(fbxMeshes.Select(mesh => mesh.name + "（" + mesh.vertexCount + " 顶点）")).ToArray();
                    int index = Array.IndexOf(fbxMeshes, selectedFbxMesh) + 1;
                    EditorGUI.BeginChangeCheck();
                    index = EditorGUILayout.Popup("恢复网格", index, labels);
                    if (EditorGUI.EndChangeCheck()) { CancelConfirmations(); selectedFbxMesh = index == 0 ? null : fbxMeshes[index - 1]; }
                }
                using (new EditorGUI.DisabledScope(!ValidTarget(target) || selectedFbxMesh == null))
                {
                    Color color = GUI.backgroundColor;
                    if (fbxArmed) GUI.backgroundColor = new Color(1f, 0.3f, 0.3f);
                    bool clicked = GUILayout.Button(fbxArmed ? "再次点击恢复 FBX 网格" : "从 FBX 恢复网格", GUILayout.Height(28));
                    GUI.backgroundColor = color;
                    if (clicked) Run(RequestFbxRestore);
                }
                GUILayout.Label(fbxArmed
                    ? "请在 5 秒内确认：恢复 FBX 原版网格与形态键，当前草稿会清空，已生成文件不会删除。"
                    : "当前网格和会话快照都丢失时仍可使用。恢复 FBX 原版形态键，不包含工具修改；请先核对网格。", EditorStyles.wordWrappedMiniLabel);
            }
        }

        private string AutoLocateFbxPath()
        {
            if (target == null) return null;
            // Follow nested prefab sources until the imported model is reached.
            Object item = target;
            var visited = new HashSet<Object>();
            while (item != null && visited.Add(item))
            {
                string path = AssetDatabase.GetAssetPath(item);
                if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return path;
                item = PrefabUtility.GetCorrespondingObjectFromSource(item);
            }
            foreach (var mesh in new[] { original, source })
            {
                string path = AssetDatabase.GetAssetPath(mesh);
                if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return path;
            }
            foreach (var animator in target.GetComponentsInParent<Animator>(true))
            {
                string path = AssetDatabase.GetAssetPath(animator.avatar);
                if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) return path;
            }
            return null;
        }

        private void FindFbx()
        {
            CancelConfirmations();
            string path = AutoLocateFbxPath();
            if (string.IsNullOrEmpty(path))
                throw new InvalidOperationException("无法从 Prefab 来源、原网格或父级 Animator 自动定位 FBX，请手动拖入原始 FBX。");
            fbxAsset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            RefreshFbxMeshes();
            SetStatus("已定位 FBX：" + path + (selectedFbxMesh == null ? "。请从列表选择要恢复的网格。" : "。匹配网格：" + selectedFbxMesh.name), MessageType.Info);
        }

        private void RefreshFbxMeshes()
        {
            selectedFbxMesh = null;
            fbxMeshes = new Mesh[0];
            if (fbxAsset == null) return;
            string path = AssetDatabase.GetAssetPath(fbxAsset);
            if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请选择项目中的原始 .fbx 模型文件。");
            fbxMeshes = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>().ToArray();
            var names = new List<string>();
            if (target != null) names.Add(target.name);
            if (!string.IsNullOrEmpty(originalMeshName)) names.Add(originalMeshName);
            if (source != null) names.Add(source.name);
            var preferred = names.SelectMany(name => new[] { name,
                System.Text.RegularExpressions.Regex.Replace(name, @"(?:_\d{6}_\d{6}(?: \d+)?|_BlendShapes|_Capped)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase) }).Distinct().ToArray();
            foreach (var comparison in new[] { StringComparison.Ordinal, StringComparison.OrdinalIgnoreCase })
                foreach (string name in preferred)
                {
                    var matches = fbxMeshes.Where(mesh => string.Equals(mesh.name, name, comparison)).ToArray();
                    if (matches.Length == 1) { selectedFbxMesh = matches[0]; return; }
                }
        }

        private void RequestFbxRestore()
        {
            if (!ValidTarget(target) || fbxAsset == null || selectedFbxMesh == null)
                throw new InvalidOperationException("请先指定 FBX 与要恢复的网格。");
            double now = EditorApplication.timeSinceStartup;
            if (!fbxArmed || now >= fbxDeadline)
            { CancelConfirmations(); fbxArmed = true; fbxDeadline = now + 5; Repaint(); return; }
            RestoreFromFbx();
        }

        private void RestoreFromFbx()
        {
            string path = AssetDatabase.GetAssetPath(fbxAsset);
            Mesh mesh = selectedFbxMesh;
            if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) || AssetDatabase.GetAssetPath(mesh) != path)
                throw new InvalidOperationException("恢复网格必须是所选 FBX 内的 Mesh。");
            var modelRenderer = fbxAsset.GetComponentsInChildren<SkinnedMeshRenderer>(true).FirstOrDefault(renderer => renderer.sharedMesh == mesh);
            if (modelRenderer == null) throw new InvalidOperationException("所选网格不是 FBX 内的蒙皮网格，请选择对应人物部件。");
            var importedBones = modelRenderer.bones;
            var currentBones = target.bones;
            if (importedBones.Length != currentBones.Length || mesh.bindposes.Length != currentBones.Length)
                throw new InvalidOperationException("FBX 与目标的骨骼数量不匹配，已停止恢复，请核对模型来源。");
            for (int i = 0; i < currentBones.Length; i++)
                if (importedBones[i] != null && currentBones[i] != null && importedBones[i].name != currentBones[i].name)
                    throw new InvalidOperationException("FBX 与目标的骨骼顺序 / 名称不匹配，已停止恢复。");
            StopPreview();
            Undo.RecordObject(target, "从 FBX 恢复网格");
            target.sharedMesh = mesh;
            target.localBounds = modelRenderer.localBounds;
            for (int i = 0; i < mesh.blendShapeCount; i++) target.SetBlendShapeWeight(i, modelRenderer.GetBlendShapeWeight(i));
            MarkTarget();
            ReleaseOriginalSnapshot();
            original = source = mesh; originalMeshName = mesh.name;
            originalWeights = Weights(target, mesh).ToList(); originalBounds = target.localBounds;
            CaptureOriginalSnapshot(); ReadShapes();
            SetStatus("已从 FBX 恢复：" + path + " / " + mesh.name + "。已设为新的恢复基准，已有生成文件保留。", MessageType.Info);
            PreviewStateChanged?.Invoke();
        }

        private void SelectOutputFolder()
        {
            string assetsRoot = Path.GetFullPath(Application.dataPath);
            string projectRoot = Path.GetDirectoryName(assetsRoot);
            string initial = Path.GetFullPath(Path.Combine(projectRoot, outputFolder ?? "Assets"));
            if (!initial.Equals(assetsRoot, StringComparison.OrdinalIgnoreCase) &&
                !initial.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                initial = assetsRoot;
            while (!Directory.Exists(initial) && !string.IsNullOrEmpty(Path.GetDirectoryName(initial)))
                initial = Path.GetDirectoryName(initial);
            string selected = EditorUtility.OpenFolderPanel("选择输出文件夹（当前项目 Assets 内）", initial, "");
            if (string.IsNullOrEmpty(selected)) return;
            string full = Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Equals(assetsRoot, StringComparison.OrdinalIgnoreCase)) outputFolder = "Assets";
            else if (full.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                outputFolder = "Assets/" + full.Substring(assetsRoot.Length + 1).Replace('\\', '/');
            else
            {
                SetStatus("请选择当前项目 Assets 内的文件夹，输出路径未更改。", MessageType.Warning);
                return;
            }
            CancelConfirmations();
            SetStatus("已选择输出文件夹：" + outputFolder, MessageType.Info);
        }

        private void LocateOutputFolder()
        {
            if (!AssetDatabase.IsValidFolder(outputFolder))
            {
                SetStatus("输出文件夹尚未创建。可先选择已有文件夹，或应用保存后再定位。", MessageType.Info);
                return;
            }
            var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(outputFolder);
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = folder;
            EditorGUIUtility.PingObject(folder);
        }

        private void RequestClearEdits()
        {
            double now = EditorApplication.timeSinceStartup;
            if (!clearArmed || now >= clearDeadline)
            { CancelConfirmations(); clearArmed = true; clearDeadline = now + 5; Repaint(); return; }
            StopPreview(); ReadShapes(); batchScale = 1; newName = "NewShape"; sparseOutput = false;
            SetStatus("已恢复默认参数，未应用的编辑已清除。已保存的网格和输出路径保持不变。", MessageType.Info);
        }

        private bool IsVisible(ShapeDraft shape) => (showDeleted || !shape.deleted) &&
            (string.IsNullOrEmpty(search) || (shape.name ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);

        private IEnumerable<ShapeDraft> Visible() => shapes.Where(IsVisible);

        private void DrawRow(ShapeDraft shape)
        {
            Color background = GUI.backgroundColor;
            if (shape.name == activePreviewShape) GUI.backgroundColor = new Color(0.55f, 0.78f, 1f);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                EditorGUI.BeginChangeCheck();
                shape.selected = GUILayout.Toggle(shape.selected, GUIContent.none, GUILayout.Width(20));
                if (EditorGUI.EndChangeCheck()) CancelConfirmations();
                using (new EditorGUI.DisabledScope(shape.deleted))
                {
                    EditorGUI.BeginChangeCheck();
                    string name = EditorGUILayout.TextField(shape.name, GUILayout.MinWidth(140));
                    float limit = EditorGUILayout.FloatField(shape.scale * 100f, GUILayout.Width(80));
                    if (EditorGUI.EndChangeCheck())
                    { StopPreview(); shape.name = name; shape.scale = BlendShapeMeshEditor.Finite(limit) ? Mathf.Clamp(limit, -1000, 1000) / 100f : 1; }
                    EditorGUI.BeginChangeCheck();
                    float mix = EditorGUILayout.FloatField(shape.mixWeight, GUILayout.Width(80));
                    shape.mixWeight = BlendShapeMeshEditor.Finite(mix) ? Mathf.Clamp(mix, 0, 100) : 100;
                    if (EditorGUI.EndChangeCheck()) CancelConfirmations();
                    using (new EditorGUI.DisabledScope(preview == null))
                    {
                        EditorGUI.BeginChangeCheck();
                        float weight = EditorGUILayout.Slider(shape.previewWeight, 0, 100, GUILayout.Width(170));
                        Rect slider = GUILayoutUtility.GetLastRect();
                        bool changed = EditorGUI.EndChangeCheck();
                        bool clicked = preview != null && Event.current.rawType == EventType.MouseDown && slider.Contains(Event.current.mousePosition);
                        if (changed)
                        { activePreviewShape = shape.name; CancelConfirmations(); shape.previewWeight = weight; Run(UpdatePreviewWeights); }
                        else if (clicked) { activePreviewShape = shape.name; PreviewStateChanged?.Invoke(); }
                    }
                }
            }
            GUI.backgroundColor = background;
            if (Event.current.type == EventType.Repaint && pendingRevealName == shape.name)
                revealRowRect = GUILayoutUtility.GetLastRect();
        }

        internal void FocusPreviewShape(string name)
        {
            int index = shapes.FindIndex(shape => !shape.deleted && string.Equals(shape.name, name, StringComparison.Ordinal));
            if (index < 0) return;
            EnsureGroupStates();
            if (!string.IsNullOrEmpty(search) && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) search = "";
            groupExpanded[index / ShapesPerGroup] = true;
            activePreviewShape = pendingRevealName = name;
            Repaint();
        }

        internal static Vector2 RevealRow(Vector2 position, Rect row, float viewportHeight)
        {
            if (viewportHeight <= 0) return position;
            if (row.yMin < position.y || row.height > viewportHeight) position.y = Mathf.Max(0, row.yMin);
            else if (row.yMax > position.y + viewportHeight) position.y = Mathf.Max(0, row.yMax - viewportHeight);
            return position;
        }

        private void Add(ShapeDraft shape)
        {
            if (string.IsNullOrWhiteSpace(shape.name) || shape.name != shape.name.Trim() || shape.name.Any(char.IsControl))
                throw new InvalidOperationException("请输入有效的新形态键名称。");
            if (shapes.Any(s => !s.deleted && s.name == shape.name)) throw new InvalidOperationException("名称已存在，请更换新形态键名称。");
            StopPreview();
            shapes.Add(shape);
            SetStatus("已添加“" + shape.name + "”，点击应用后保存。", MessageType.Info);
        }

        private void StartPreview()
        {
            CancelConfirmations();
            if (!Current) throw new InvalidOperationException("目标网格已改变，请重新读取。");
            BlendShapeMeshEditor.Validate(source, shapes);
            previewOriginalWeights = Weights(target, source);
            previewOriginalBounds = target.localBounds;
            Mesh built = BlendShapeMeshEditor.Build(source, shapes, sparseOutput);
            built.hideFlags = HideFlags.HideAndDontSave;
            preview = built;
            target.sharedMesh = preview;
            Bounds bounds = previewOriginalBounds; bounds.Encapsulate(preview.bounds);
            target.localBounds = bounds;
            UpdatePreviewWeights();
            SceneView.RepaintAll();
        }

        private void UpdatePreviewWeights()
        {
            if (target == null || preview == null || target.sharedMesh != preview) return;
            int i = 0;
            foreach (var shape in shapes.Where(s => !s.deleted)) target.SetBlendShapeWeight(i++, shape.previewWeight);
            SceneView.RepaintAll();
            PreviewStateChanged?.Invoke();
        }

        private void StopPreview()
        {
            CancelConfirmations();
            if (preview == null) return;
            if (target != null && target.sharedMesh == preview)
            {
                target.sharedMesh = source;
                target.localBounds = previewOriginalBounds;
                if (source != null && previewOriginalWeights != null)
                    for (int i = 0; i < Math.Min(source.blendShapeCount, previewOriginalWeights.Length); i++)
                        target.SetBlendShapeWeight(i, previewOriginalWeights[i]);
            }
            Object.DestroyImmediate(preview);
            preview = null; previewOriginalWeights = null;
            SceneView.RepaintAll();
            PreviewStateChanged?.Invoke();
        }

        private string PrepareOutputFolder()
        {
            string folder = (outputFolder ?? "").Trim().Replace('\\', '/').TrimEnd('/');
            if ((folder != "Assets" && !folder.StartsWith("Assets/", StringComparison.Ordinal)) ||
                folder.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part == "." || part == ".." ||
                    part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new InvalidOperationException("输出文件夹必须位于 Assets 下，例如 Assets/BlendShapeMeshes。");
            string current = "Assets";
            foreach (string part in folder.Split('/').Skip(1))
            {
                string next = current + "/" + part;
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, part);
                current = next;
            }
            return folder;
        }

        private string SaveGeneratedMesh(Mesh mesh)
        {
            string folder = PrepareOutputFolder();
            string baseName = string.IsNullOrWhiteSpace(originalMeshName) ? "Mesh" : originalMeshName;
            string safeName = string.Concat(baseName.Select(c => Path.GetInvalidFileNameChars().Contains(c) || char.IsControl(c) ? '_' : c));
            safeName = safeName.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(safeName)) safeName = "Mesh";
            string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + safeName + "_" + DateTime.Now.ToString("yyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".asset");
            mesh.name = Path.GetFileNameWithoutExtension(path);
            try
            {
                AssetDatabase.CreateAsset(mesh, path);
                AssetDatabase.SaveAssetIfDirty(mesh);
                if (AssetDatabase.LoadAssetAtPath<Mesh>(path) != mesh)
                    throw new IOException("新网格保存失败，目标未替换。");
            }
            catch
            {
                if (AssetDatabase.GetAssetPath(mesh) == path) AssetDatabase.DeleteAsset(path);
                throw;
            }
            return path;
        }

        private void Apply()
        {
            if (applyInProgress || ApplyCooldownSeconds > 0)
                throw new InvalidOperationException("保存正在进行或处于 3 秒冷却，请稍后再试。");
            applyInProgress = true;
            applyReadyAt = EditorApplication.timeSinceStartup + 3;
            CancelConfirmations();
            Repaint();
            try { ApplyCore(); }
            finally
            {
                applyInProgress = false;
                applyReadyAt = EditorApplication.timeSinceStartup + 3;
                Repaint();
            }
        }

        private bool HasSessionOutput => sessionOutput != null && AssetDatabase.Contains(sessionOutput) &&
            !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(sessionOutput));

        private void ApplyCore()
        {
            if (!CanEdit) throw new InvalidOperationException("请选择有效且未被外部替换的场景网格；丢失网格时请先恢复原始网格或从 FBX 恢复。");
            BlendShapeMeshEditor.Validate(source, shapes);
            StopPreview();
            float[] weights = Weights(target, source);
            var active = shapes.Where(shape => !shape.deleted).ToArray();
            var newWeights = active.Select(shape => shape.sourceIndex >= 0 ? weights[shape.sourceIndex] : 0).ToArray();
            Mesh built = null;
            Mesh output = null;
            string createdPath = null;
            bool overwrite = HasSessionOutput;
            int group = -1;
            try
            {
                built = BlendShapeMeshEditor.Build(source, shapes, sparseOutput);
                Undo.IncrementCurrentGroup(); group = Undo.GetCurrentGroup();
                Undo.SetCurrentGroupName("应用形态键编辑");
                Undo.RecordObject(target, "应用形态键编辑");
                if (overwrite)
                {
                    output = sessionOutput;
                    Undo.RegisterCompleteObjectUndo(output, "更新本次生成网格");
                    built.name = output.name;
                    EditorUtility.CopySerialized(built, output);
                    EditorUtility.SetDirty(output);
                    AssetDatabase.SaveAssetIfDirty(output);
                    Object.DestroyImmediate(built); built = null;
                }
                else
                {
                    createdPath = SaveGeneratedMesh(built);
                    output = built;
                }
                target.sharedMesh = output;
                for (int i = 0; i < newWeights.Length; i++) target.SetBlendShapeWeight(i, newWeights[i]);
                Bounds bounds = target.localBounds; bounds.Encapsulate(output.bounds); target.localBounds = bounds;
                MarkTarget();
                Undo.CollapseUndoOperations(group);
                sessionOutput = output; source = output; ReadShapes();
                SetStatus((overwrite ? "已覆盖本次生成网格：" : "已保存并替换：") + AssetDatabase.GetAssetPath(output) + "。支持 Ctrl+Z，原始恢复不会删除生成文件。", MessageType.Info);
                EditorGUIUtility.PingObject(output);
            }
            catch
            {
                if (group >= 0) Undo.RevertAllDownToGroup(group);
                if (overwrite && output != null) AssetDatabase.SaveAssetIfDirty(output);
                if (!string.IsNullOrEmpty(createdPath)) AssetDatabase.DeleteAsset(createdPath);
                else if (built != null && !EditorUtility.IsPersistent(built)) Object.DestroyImmediate(built);
                throw;
            }
        }

        internal Dictionary<string, float> ReadPreviewValues()
        {
            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            if (PreviewUnlocked)
                foreach (var shape in shapes.Where(shape => !shape.deleted)) values[shape.name] = shape.previewWeight;
            return values;
        }

        internal void SetPreviewValues(Dictionary<string, float> values)
        {
            if (!PreviewUnlocked) return;
            foreach (var shape in shapes.Where(shape => !shape.deleted))
                if (values.TryGetValue(shape.name, out float value)) shape.previewWeight = value;
            UpdatePreviewWeights(); Repaint();
        }

        private void RestoreOriginal()
        {
            if (!CanRestoreOriginal) throw new InvalidOperationException("没有可恢复的原始网格或会话快照。");
            StopPreview();
            Mesh recovery = original;
            string recoveredPath = null;
            if (recovery == null)
            {
                recovery = Object.Instantiate(originalSnapshot);
                recovery.hideFlags = HideFlags.None;
                try { recoveredPath = SaveGeneratedMesh(recovery); }
                catch
                {
                    if (recovery != null && !EditorUtility.IsPersistent(recovery)) Object.DestroyImmediate(recovery);
                    throw;
                }
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("恢复到原始网格-未修改");
            try
            {
                Undo.RecordObject(target, "恢复到原始网格-未修改");
                target.sharedMesh = recovery;
                target.localBounds = originalBounds;
                for (int i = 0; i < Math.Min(originalWeights.Count, recovery.blendShapeCount); i++)
                    target.SetBlendShapeWeight(i, originalWeights[i]);
                MarkTarget();
                Undo.CollapseUndoOperations(group);
                original = recovery; source = recovery; ReadShapes();
                SetStatus(recoveredPath == null
                    ? "已恢复拖入时的原始网格、权重和包围盒。此操作不会删除已生成的文件。"
                    : "原始资源已丢失，已从会话快照另存并恢复：" + recoveredPath + "。已有生成文件保留。", MessageType.Info);
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }

        private void MarkTarget()
        {
            EditorUtility.SetDirty(target);
            PrefabUtility.RecordPrefabInstancePropertyModifications(target);
            EditorSceneManager.MarkSceneDirty(target.gameObject.scene);
            SceneView.RepaintAll();
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                StopPreview();
                SetStatus(error.Message, MessageType.Error);
                if (!(error is InvalidOperationException) && !(error is IOException)) Debug.LogException(error);
            }
        }

        private void SetStatus(string message, MessageType type) { status = message; statusType = type; Repaint(); }
    }

    [Serializable]
    internal sealed class ShapeSample
    {
        public int sourceIndex;
        public float weight, coefficient;
        public ShapeSample(int index, float value, float scale)
        { sourceIndex = index; weight = value; coefficient = scale; }
    }

    [Serializable]
    internal sealed class ShapeDraft
    {
        public int sourceIndex = -1;
        public string name;
        public float scale = 1, mixWeight = 100, previewWeight;
        public bool selected, deleted;
        // Mixtures store snapshots of source samples, not references to mutable draft rows.
        public List<ShapeSample> samples = new List<ShapeSample>();
    }

    internal static class BlendShapeMeshEditor
    {
        internal const string Version = "1.6.1";

        internal static void Validate(Mesh source, IList<ShapeDraft> shapes)
        {
            if (source == null) throw new InvalidOperationException("未选择源网格。");
            if (!source.isReadable) throw new InvalidOperationException("网格不可读，请在模型导入设置中开启 Read/Write。");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var shape in shapes.Where(s => !s.deleted))
            {
                if (string.IsNullOrWhiteSpace(shape.name) || shape.name != shape.name.Trim())
                    throw new InvalidOperationException("形态键名称不能为空，也不能以空白开头或结尾。");
                if (shape.name.Any(char.IsControl)) throw new InvalidOperationException("形态键名称不能包含控制字符。");
                if (!names.Add(shape.name)) throw new InvalidOperationException("形态键重名：" + shape.name);
                if (!Finite(shape.scale) || shape.scale < -10 || shape.scale > 10)
                    throw new InvalidOperationException("变形幅度必须在 −10～10 倍之间。");
                if (shape.sourceIndex < -1 || shape.sourceIndex >= source.blendShapeCount)
                    throw new InvalidOperationException("源形态键索引已失效，请重新读取网格。");
                foreach (var sample in shape.samples)
                    if (sample.sourceIndex < 0 || sample.sourceIndex >= source.blendShapeCount ||
                        !Finite(sample.weight) || !Finite(sample.coefficient))
                        throw new InvalidOperationException("混合配方包含失效的源数据。");
            }
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static ShapeDraft Mix(IEnumerable<ShapeDraft> inputs, string name)
        {
            var result = new ShapeDraft { name = name };
            foreach (var input in inputs.Where(s => !s.deleted))
            {
                if (input.sourceIndex >= 0)
                    result.samples.Add(new ShapeSample(input.sourceIndex, input.mixWeight, input.scale));
                else
                    foreach (var sample in input.samples)
                        result.samples.Add(new ShapeSample(sample.sourceIndex, sample.weight,
                            sample.coefficient * input.scale * input.mixWeight / 100f));
            }
            return result;
        }

        // Use each shape's own frame count. Include the implicit zero deformation at weight zero.
        // Interpolate between frames, and extrapolate with the nearest interval beyond the endpoints.
        internal static void Sample(Mesh source, int shapeIndex, float weight, Vector3[] v, Vector3[] n, Vector3[] t,
            Vector3[] scratchV = null, Vector3[] scratchN = null, Vector3[] scratchT = null)
        {
            Array.Clear(v, 0, v.Length); Array.Clear(n, 0, n.Length); Array.Clear(t, 0, t.Length);
            var frames = new List<KeyValuePair<float, int>>();
            for (int i = 0; i < source.GetBlendShapeFrameCount(shapeIndex); i++)
                frames.Add(new KeyValuePair<float, int>(source.GetBlendShapeFrameWeight(shapeIndex, i), i));
            if (!frames.Any(f => f.Key == 0)) frames.Add(new KeyValuePair<float, int>(0, -1));
            frames.Sort((a, b) => a.Key.CompareTo(b.Key));
            if (frames.Count < 2) throw new InvalidOperationException("源形态键缺少可用的非零权重帧。");
            int upper = frames.FindIndex(f => f.Key >= weight);
            upper = upper < 0 ? frames.Count - 1 : Mathf.Clamp(upper, 1, frames.Count - 1);
            var a = frames[upper - 1]; var b = frames[upper];
            float rate = (weight - a.Key) / (b.Key - a.Key);
            if (a.Value >= 0) source.GetBlendShapeFrameVertices(shapeIndex, a.Value, v, n, t);
            var bv = scratchV ?? new Vector3[v.Length];
            var bn = scratchN ?? new Vector3[v.Length];
            var bt = scratchT ?? new Vector3[v.Length];
            Array.Clear(bv, 0, bv.Length); Array.Clear(bn, 0, bn.Length); Array.Clear(bt, 0, bt.Length);
            if (b.Value >= 0) source.GetBlendShapeFrameVertices(shapeIndex, b.Value, bv, bn, bt);
            for (int i = 0; i < v.Length; i++)
            {
                v[i] = Vector3.LerpUnclamped(v[i], bv[i], rate);
                n[i] = Vector3.LerpUnclamped(n[i], bn[i], rate);
                t[i] = Vector3.LerpUnclamped(t[i], bt[i], rate);
            }
        }

        internal static Mesh Build(Mesh source, IList<ShapeDraft> shapes, bool sparseOutput = false)
        {
            Validate(source, shapes);
            // Clone all vertex streams, UV dimensions, topology, base vertices, bind poses and modern bone weights.
            Mesh output = Object.Instantiate(source);
            output.name = source.name + "_BlendShapes";
            output.hideFlags = HideFlags.None;
            output.ClearBlendShapes();
            int count = source.vertexCount;
            var v = new Vector3[count]; var n = new Vector3[count]; var t = new Vector3[count];
            var sumMin = new Vector3[count]; var sumMax = new Vector3[count];
            var shapeMin = new Vector3[count]; var shapeMax = new Vector3[count];
            Vector3[] sv = null, sn = null, st = null, scratchV = null, scratchN = null, scratchT = null;
            try
            {
                foreach (var shape in shapes.Where(s => !s.deleted))
                {
                    Array.Clear(shapeMin, 0, count); Array.Clear(shapeMax, 0, count);
                    if (shape.sourceIndex >= 0)
                    {
                        int frames = source.GetBlendShapeFrameCount(shape.sourceIndex);
                        for (int frame = 0; frame < frames; frame++)
                        {
                            source.GetBlendShapeFrameVertices(shape.sourceIndex, frame, v, n, t);
                            Scale(v, n, t, shape.scale);
                            if (sparseOutput) { Array.Clear(n, 0, count); Array.Clear(t, 0, count); }
                            output.AddBlendShapeFrame(shape.name, source.GetBlendShapeFrameWeight(shape.sourceIndex, frame), v, n, t);
                            Extend(shapeMin, shapeMax, v);
                        }
                        // Include extrapolation to 100 when imported frame endpoints are below the UI range.
                        if (source.GetBlendShapeFrameWeight(shape.sourceIndex, frames - 1) < 100)
                        {
                            if (scratchV == null) { scratchV = new Vector3[count]; scratchN = new Vector3[count]; scratchT = new Vector3[count]; }
                            Sample(source, shape.sourceIndex, 100, v, n, t, scratchV, scratchN, scratchT);
                            Scale(v, n, t, shape.scale);
                            Extend(shapeMin, shapeMax, v);
                        }
                    }
                    else
                    {
                        Array.Clear(v, 0, count); Array.Clear(n, 0, count); Array.Clear(t, 0, count);
                        if (shape.samples.Count > 0)
                        {
                            if (sv == null) { sv = new Vector3[count]; sn = new Vector3[count]; st = new Vector3[count]; }
                            if (scratchV == null) { scratchV = new Vector3[count]; scratchN = new Vector3[count]; scratchT = new Vector3[count]; }
                            foreach (var sample in shape.samples)
                            {
                                Sample(source, sample.sourceIndex, sample.weight, sv, sn, st, scratchV, scratchN, scratchT);
                                float factor = sample.coefficient * shape.scale;
                                for (int i = 0; i < count; i++)
                                { v[i] += sv[i] * factor; n[i] += sn[i] * factor; t[i] += st[i] * factor; }
                            }
                        }
                        if (sparseOutput) { Array.Clear(n, 0, count); Array.Clear(t, 0, count); }
                        output.AddBlendShapeFrame(shape.name, 100, v, n, t);
                        Extend(shapeMin, shapeMax, v);
                    }
                    for (int i = 0; i < count; i++) { sumMin[i] += shapeMin[i]; sumMax[i] += shapeMax[i]; }
                }
                Bounds bounds = source.bounds;
                var vertices = source.vertices;
                for (int i = 0; i < count; i++)
                { bounds.Encapsulate(vertices[i] + sumMin[i]); bounds.Encapsulate(vertices[i] + sumMax[i]); }
                output.bounds = bounds;
                return output;
            }
            catch { Object.DestroyImmediate(output); throw; }
        }

        private static void Scale(Vector3[] v, Vector3[] n, Vector3[] t, float scale)
        { for (int i = 0; i < v.Length; i++) { v[i] *= scale; n[i] *= scale; t[i] *= scale; } }

        private static void Extend(Vector3[] min, Vector3[] max, Vector3[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!Finite(values[i].x) || !Finite(values[i].y) || !Finite(values[i].z))
                    throw new InvalidOperationException("变形计算超出有效数值范围。");
                min[i] = Vector3.Min(min[i], values[i]); max[i] = Vector3.Max(max[i], values[i]);
            }
        }
    }
}
#endif
