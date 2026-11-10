// LGC BlendShape Editor v1.6.1. Secondary renderer preview only; no mesh editing or asset generation.
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace LGC.BlendShapeTools
{
    public sealed class BlendShapePreviewWindow : EditorWindow
    {
        private static BlendShapePreviewWindow instance;
        [SerializeField] private SkinnedMeshRenderer target;
        [SerializeField] private Mesh boundMesh;
        [SerializeField] private List<float> initialWeights = new List<float>();
        [SerializeField] private List<float> previewWeights = new List<float>();
        [SerializeField] private bool syncEnabled;
        [SerializeField] private string search = "";
        private const int ShapesPerGroup = 20;
        [SerializeField] private List<bool> groupExpanded = new List<bool>();
        private Vector2 scroll;
        private string activeShapeName, pendingRevealName;
        private Rect revealRowRect;
        private string status;
        private static bool dockPending;

        public static void Open()
        {
            var main = BlendShapeEditorWindow.ActiveWindow;
            var window = GetWindow<BlendShapePreviewWindow>();
            window.minSize = new Vector2(580, 360);
            window.Show();
            if (main == null || dockPending) return;
            // Move tabs only after OnGUI has finished, so the source GUI stack stays valid.
            dockPending = true;
            EditorApplication.delayCall += () =>
            {
                dockPending = false;
                if (main == null || window == null) return;
                if (!BlendShapePreviewDocking.TryDockRight(main, window, out string reason))
                    window.status = "自动右侧停靠未完成：" + reason + "。可将模型2标签手动拖到主工具右侧。";
                window.Repaint();
            };
        }

        internal static void BeforeMainBind(SkinnedMeshRenderer renderer)
        {
            if (instance != null && renderer != null && instance.target == renderer)
            {
                instance.Bind(null);
                instance.status = "该对象已转为主工具目标，模型2引用已清空，请拖入其他对象。";
            }
        }

        private void OnEnable()
        {
            instance = this;
            titleContent = new GUIContent("预览模型2 v" + BlendShapeMeshEditor.Version);
            minSize = new Vector2(580, 360);
            AssemblyReloadEvents.beforeAssemblyReload += RestoreInitialWeights;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorApplication.playModeStateChanged += OnPlayMode;
            BlendShapeEditorWindow.PreviewStateChanged += SyncFromMain;
        }

        private void OnDisable()
        {
            RestoreInitialWeights();
            AssemblyReloadEvents.beforeAssemblyReload -= RestoreInitialWeights;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorApplication.playModeStateChanged -= OnPlayMode;
            BlendShapeEditorWindow.PreviewStateChanged -= SyncFromMain;
            if (instance == this) instance = null;
        }

        private void OnDestroy()
        {
            target = null; boundMesh = null;
            initialWeights.Clear(); previewWeights.Clear(); groupExpanded.Clear(); syncEnabled = false; search = "";
            activeShapeName = pendingRevealName = null;
        }

        private void OnSceneSaving(Scene scene, string path) { RestoreInitialWeights(); }
        private void OnPlayMode(PlayModeStateChange state)
        { if (state == PlayModeStateChange.ExitingEditMode) RestoreInitialWeights(); }

        private bool Current => target != null && boundMesh != null && target.sharedMesh == boundMesh;

        private void Bind(SkinnedMeshRenderer renderer)
        {
            if (renderer != null)
            {
                if (EditorUtility.IsPersistent(renderer) || !renderer.gameObject.scene.IsValid() || EditorSceneManager.IsPreviewScene(renderer.gameObject.scene))
                { status = "请拖入普通场景中的 SkinnedMeshRenderer。"; return; }
                if (BlendShapeEditorWindow.ActiveWindow != null && renderer == BlendShapeEditorWindow.ActiveWindow.TargetRenderer)
                { status = "模型2需要使用与主工具不同的 Renderer，避免预览互相覆盖。"; return; }
            }
            RestoreInitialWeights();
            target = renderer; boundMesh = renderer != null ? renderer.sharedMesh : null;
            initialWeights = new List<float>(); previewWeights = new List<float>();
            if (boundMesh != null)
                for (int i = 0; i < boundMesh.blendShapeCount; i++) initialWeights.Add(renderer.GetBlendShapeWeight(i));
            previewWeights.AddRange(initialWeights);
            search = ""; scroll = Vector2.zero; status = null;
            activeShapeName = pendingRevealName = null; groupExpanded.Clear();
            EnsureGroupStates();
            if (syncEnabled) SyncFromMain();
        }

        private void RestoreInitialWeights()
        {
            if (Current)
                for (int i = 0; i < Math.Min(initialWeights.Count, boundMesh.blendShapeCount); i++)
                    target.SetBlendShapeWeight(i, initialWeights[i]);
            previewWeights = new List<float>(initialWeights);
            SceneView.RepaintAll(); Repaint();
        }

        private void ApplyWeights()
        {
            if (!Current || EditorApplication.isPlayingOrWillChangePlaymode) return;
            for (int i = 0; i < previewWeights.Count; i++) target.SetBlendShapeWeight(i, previewWeights[i]);
            SceneView.RepaintAll();
        }

        private void SetAll(bool defaults)
        {
            if (!Current) return;
            for (int i = 0; i < previewWeights.Count; i++) previewWeights[i] = defaults ? initialWeights[i] : 0;
            ApplyWeights(); SendToMain(); Repaint();
        }

        private void SendToMain()
        {
            var main = BlendShapeEditorWindow.ActiveWindow;
            if (!syncEnabled || !Current || main == null || !main.PreviewUnlocked || main.TargetRenderer == target) return;
            var values = new Dictionary<string, float>(StringComparer.Ordinal);
            for (int i = 0; i < previewWeights.Count; i++) values[boundMesh.GetBlendShapeName(i)] = previewWeights[i];
            if (!string.IsNullOrEmpty(activeShapeName)) main.FocusPreviewShape(activeShapeName);
            main.SetPreviewValues(values);
        }

        private void SyncFromMain()
        {
            var main = BlendShapeEditorWindow.ActiveWindow;
            if (!syncEnabled || !Current || main == null || !main.PreviewUnlocked || main.TargetRenderer == target || EditorApplication.isPlayingOrWillChangePlaymode)
            { Repaint(); return; }
            var values = main.ReadPreviewValues();
            for (int i = 0; i < previewWeights.Count; i++)
                if (values.TryGetValue(boundMesh.GetBlendShapeName(i), out float value)) previewWeights[i] = value;
            ApplyWeights();
            if (!string.IsNullOrEmpty(main.ActivePreviewShape)) FocusPreviewShape(main.ActivePreviewShape);
            Repaint();
        }

        private void EnsureGroupStates()
        {
            int count = Current ? (boundMesh.blendShapeCount + ShapesPerGroup - 1) / ShapesPerGroup : 0;
            while (groupExpanded.Count < count) groupExpanded.Add(groupExpanded.Count == 0);
            if (groupExpanded.Count > count) groupExpanded.RemoveRange(count, groupExpanded.Count - count);
        }

        private void SetAllGroupsExpanded(bool expanded)
        {
            EnsureGroupStates();
            for (int i = 0; i < groupExpanded.Count; i++) groupExpanded[i] = expanded;
        }

        private void FocusPreviewShape(string name)
        {
            if (!Current) return;
            int index = boundMesh.GetBlendShapeIndex(name);
            if (index < 0) return;
            EnsureGroupStates();
            if (!string.IsNullOrEmpty(search) && name.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) search = "";
            groupExpanded[index / ShapesPerGroup] = true;
            activeShapeName = pendingRevealName = name;
            Repaint();
        }

        private void CopyShapeName(string name)
        {
            EditorGUIUtility.systemCopyBuffer = name;
            status = "已复制形态键名称：" + name;
        }

        private void DrawRow(int index)
        {
            string name = boundMesh.GetBlendShapeName(index);
            Color color = GUI.backgroundColor;
            if (activeShapeName == name) GUI.backgroundColor = new Color(0.55f, 0.78f, 1f);
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                if (GUILayout.Button(new GUIContent(name, "点击复制形态键名称"), EditorStyles.label, GUILayout.Height(EditorGUIUtility.singleLineHeight), GUILayout.MinWidth(150), GUILayout.ExpandWidth(true)))
                    CopyShapeName(name);
                EditorGUI.BeginChangeCheck();
                float value = EditorGUILayout.Slider(previewWeights[index], 0, 100, GUILayout.MinWidth(200));
                Rect slider = GUILayoutUtility.GetLastRect();
                bool changed = EditorGUI.EndChangeCheck();
                bool clicked = Event.current.rawType == EventType.MouseDown && slider.Contains(Event.current.mousePosition);
                if (changed || clicked)
                {
                    activeShapeName = name;
                    if (changed) { previewWeights[index] = value; ApplyWeights(); }
                    SendToMain();
                }
            }
            GUI.backgroundColor = color;
            if (Event.current.type == EventType.Repaint && pendingRevealName == name)
                revealRowRect = GUILayoutUtility.GetLastRect();
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(6, 6, Mathf.Max(1, position.width - 12), Mathf.Max(1, position.height - 12)));
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                var next = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("模型2网格", target, typeof(SkinnedMeshRenderer), true);
                if (next != target) Bind(next);
                EditorGUILayout.HelpBox("仅预览，不修改网格资源。默认值是拖入时记录的状态；关闭窗口、保存场景或进入运行前恢复该状态。", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(!Current))
                    {
                        if (GUILayout.Button("恢复拖入时数值", GUILayout.Height(26))) SetAll(true);
                        if (GUILayout.Button("全部归零", GUILayout.Height(26))) SetAll(false);
                    }
                    EditorGUI.BeginChangeCheck();
                    syncEnabled = GUILayout.Toggle(syncEnabled, "启用同步", GUILayout.Width(90));
                    if (EditorGUI.EndChangeCheck() && syncEnabled) SyncFromMain();
                }
                if (syncEnabled)
                {
                    var main = BlendShapeEditorWindow.ActiveWindow;
                    GUILayout.Label(main != null && main.PreviewUnlocked
                        ? "按同名形态键同步数值；调整滑条时，另一窗口自动展开并定位高亮该项。无同名项保持不变。"
                        : "请先在主工具中解锁预览，同步不会自动创建主工具的预览网格。", EditorStyles.wordWrappedMiniLabel);
                }
                if (target != null && !Current)
                    EditorGUILayout.HelpBox("网格丢失或已被替换，请重新拖入有效网格。", MessageType.Warning);
                search = EditorGUILayout.TextField("搜索", search);
                EnsureGroupStates();
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("每 20 项一组", EditorStyles.miniLabel);
                    GUILayout.FlexibleSpace();
                    using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(search)))
                    {
                        if (GUILayout.Button("全部展开", GUILayout.Width(85))) SetAllGroupsExpanded(true);
                        if (GUILayout.Button("全部折叠", GUILayout.Width(85))) SetAllGroupsExpanded(false);
                    }
                }
                if (Event.current.type == EventType.Repaint) revealRowRect = Rect.zero;
                scroll = EditorGUILayout.BeginScrollView(scroll, EditorStyles.helpBox, GUILayout.ExpandHeight(true));
                if (Current)
                {
                    bool searching = !string.IsNullOrEmpty(search);
                    bool anyVisible = false;
                    for (int group = 0; group < groupExpanded.Count; group++)
                    {
                        int start = group * ShapesPerGroup;
                        int count = Math.Min(ShapesPerGroup, boundMesh.blendShapeCount - start);
                        var matches = Enumerable.Range(start, count).Where(index => !searching || boundMesh.GetBlendShapeName(index).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToArray();
                        if (matches.Length == 0) continue;
                        anyVisible = true;
                        using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
                        using (new EditorGUI.DisabledScope(searching))
                        {
                            bool expanded = EditorGUILayout.Foldout(searching || groupExpanded[group],
                                $"第 {group + 1} 组 · {start + 1}–{start + count} · {matches.Length} 项", true);
                            if (!searching) groupExpanded[group] = expanded;
                        }
                        if (searching || groupExpanded[group]) foreach (int index in matches) DrawRow(index);
                    }
                    if (!anyVisible) GUILayout.Label("没有符合搜索条件的形态键。", EditorStyles.centeredGreyMiniLabel);
                }
                else GUILayout.Label("拖入模型2的网格以开始预览。", EditorStyles.centeredGreyMiniLabel);
                EditorGUILayout.EndScrollView();
                if (Event.current.type == EventType.Repaint && pendingRevealName != null && revealRowRect.height > 0)
                {
                    scroll = BlendShapeEditorWindow.RevealRow(scroll, revealRowRect, GUILayoutUtility.GetLastRect().height);
                    pendingRevealName = null; Repaint();
                }
                if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
            }
            GUILayout.Label("v" + BlendShapeMeshEditor.Version, EditorStyles.miniLabel);
            GUILayout.EndArea();
        }
    }

    // v1.6.1: reflection is limited to Unity's own tab-drop operation; window state stays in its EditorWindow.
    internal static class BlendShapePreviewDocking
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type DockType = typeof(EditorWindow).Assembly.GetType("UnityEditor.DockArea");
        private static readonly Type SplitType = typeof(EditorWindow).Assembly.GetType("UnityEditor.SplitView");
        private static readonly FieldInfo WindowParent = typeof(EditorWindow).GetField("m_Parent", Members);

        private static object Read(object value, string name)
        {
            if (value == null) return null;
            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                var property = type.GetProperty(name, Members | BindingFlags.DeclaredOnly);
                if (property != null) return property.GetValue(value);
                var field = type.GetField(name, Members | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(value);
            }
            throw new MissingMemberException(value.GetType().FullName, name);
        }

        internal static bool IsDockedRight(EditorWindow main, EditorWindow preview)
        {
            if (main == null || preview == null || WindowParent == null || DockType == null || SplitType == null) return false;
            object left = WindowParent.GetValue(main), right = WindowParent.GetValue(preview);
            if (left == null || right == null || left == right) return false;
            object split = Read(left, "parent");
            if (split == null || !SplitType.IsInstanceOfType(split) || !ReferenceEquals(split, Read(right, "parent")) || (bool)Read(split, "vertical")) return false;
            var children = (Array)Read(split, "children");
            int leftIndex = Array.IndexOf(children, left);
            return leftIndex >= 0 && leftIndex + 1 < children.Length && ReferenceEquals(children.GetValue(leftIndex + 1), right);
        }

        internal static bool TryDockRight(EditorWindow main, EditorWindow preview, out string reason)
        {
            reason = null;
            try
            {
                if (main == null || preview == null || main == preview)
                    throw new InvalidOperationException("找不到有效的主窗口或模型2窗口");
                if (DockType == null || SplitType == null || WindowParent == null)
                    throw new NotSupportedException("当前 Unity 的停靠接口不可用");
                if (IsDockedRight(main, preview))
                {
                    main.ShowTab(); preview.ShowTab(); preview.Focus();
                    return true; // Preserve the divider position the user has already chosen.
                }
                object mainDock = WindowParent.GetValue(main), sourceDock = WindowParent.GetValue(preview);
                if (!DockType.IsInstanceOfType(mainDock) || !DockType.IsInstanceOfType(sourceDock))
                    throw new InvalidOperationException("请使用普通可停靠编辑器窗口");
                object split = Read(mainDock, "parent");
                if (split == null || !SplitType.IsInstanceOfType(split))
                    throw new NotSupportedException("主窗口没有可用的分栏容器");
                var dragOver = SplitType.GetMethod("DragOver", Members, null, new[] { typeof(EditorWindow), typeof(Vector2) }, null);
                var performDrop = SplitType.GetMethods(Members).SingleOrDefault(method => method.Name == "PerformDrop" && method.GetParameters().Length == 3);
                var dragSource = DockType.GetField("s_OriginalDragSource", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (dragOver == null || performDrop == null || dragSource == null)
                    throw new NotSupportedException("当前 Unity 的标签停靠接口不兼容");
                // Only grow a standalone container belonging to this tool; leave the main editor container alone.
                var children = (Array)Read(split, "children");
                object container = Read(mainDock, "window");
                if (children.Length == 1 && ReferenceEquals(children.GetValue(0), mainDock) && ReferenceEquals(Read(container, "rootView"), split))
                {
                    PropertyInfo position = container.GetType().GetProperty("position", Members);
                    MethodInfo resize = container.GetType().GetMethod("OnResize", Members, null, Type.EmptyTypes, null);
                    if (position != null && resize != null)
                    {
                        Rect rect = (Rect)position.GetValue(container);
                        float desired = Mathf.Max(main.minSize.x * 1.5f, preview.minSize.x * 3f) + 12;
                        if (rect.width < desired)
                        {
                            rect.width = desired;
                            position.SetValue(container, rect);
                            resize.Invoke(container, null);
                        }
                    }
                }
                Rect bounds = (Rect)Read(mainDock, "screenPosition");
                float leftMinimum = ((Vector2)Read(mainDock, "minSize")).x;
                float rightMinimum = ((Vector2)Read(sourceDock, "minSize")).x;
                if (bounds.width < leftMinimum + rightMinimum)
                    throw new InvalidOperationException("主工具区域太窄，请先放大窗口再重试");
                Vector2 point = new Vector2(bounds.xMax - 4, bounds.center.y);
                object info = dragOver.Invoke(split, new object[] { preview, point });
                if (info == null) throw new InvalidOperationException("右侧没有可用停靠区域");
                object extra = Read(info, "userData");
                if (extra == null || Convert.ToInt32(Read(extra, "edge")) != 8 || !ReferenceEquals(Read(info, "dropArea"), split))
                    throw new InvalidOperationException("未能确认右侧分栏位置");
                var dropRect = info.GetType().GetField("rect", Members);
                if (dropRect == null) throw new MissingMemberException("DropInfo.rect");
                float width = Mathf.Clamp(bounds.width / 3f, rightMinimum, bounds.width - leftMinimum);
                dropRect.SetValue(info, new Rect(bounds.xMax - width, bounds.y, width, bounds.height));
                object previousDragSource = dragSource.GetValue(null);
                try
                {
                    dragSource.SetValue(null, sourceDock);
                    if (!(bool)performDrop.Invoke(split, new object[] { preview, info, point }))
                        throw new InvalidOperationException("Unity 未接受右侧停靠");
                }
                finally { dragSource.SetValue(null, previousDragSource); }
                if (!IsDockedRight(main, preview)) throw new InvalidOperationException("未形成预期的左右布局");
                main.ShowTab(); preview.ShowTab();
                main.Repaint(); preview.Repaint();
                return true;
            }
            catch (Exception error)
            {
                Exception detail = error is TargetInvocationException && error.InnerException != null ? error.InnerException : error;
                reason = detail.Message;
                // No close/recreation: existing target, weight snapshot and synchronisation state remain intact.
                if (preview != null && WindowParent != null && WindowParent.GetValue(preview) == null) preview.Show();
                return false;
            }
        }
    }

}
#endif
