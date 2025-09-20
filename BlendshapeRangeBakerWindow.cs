#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using UnityEditor.IMGUI.Controls;
using System.Collections.Generic;
using System.Linq;
using System.IO;

public class BlendshapeRangeBakerWindow : EditorWindow
{
    // ========= 数据结构 =========
    [System.Serializable]
    private class Entry
    {
        public int blendshapeIndex = -1;
        public string displayName = "";
        [Range(0f, 100f)] public float newMaxPercent = 65f; // 新 100% = 旧 x%
    }

    // 批量设定的持久数值（默认 65）
    private float batchPercent = 65f;

    // ========= 目标对象 =========
    private SkinnedMeshRenderer smr;
    private Mesh srcMesh;

    // ========= 基础配置 =========
    private string saveFolder = "Assets/EditedMeshes";
    private bool autoAssignToSMR = true;

    // ========= BlendShape 列表与条目 =========
    private string[] bsNames = new string[0];
    private readonly List<Entry> entries = new();

    // ========= 顶部 Toolbar =========
    private enum Tab { 选择, 设置, 输出 }
    private Tab currentTab = Tab.选择;
    private readonly string[] tabNames = new[] { "选择", "设置", "输出" };

    // ========= 滚动与 UI 状态 =========
    private Vector2 outerScroll;
    private Vector2 scrollPicker;
    private Vector2 scrollEntries;
    private Vector2 scrollOutput;

    // 选择 搜索/勾选
    private SearchField searchField;
    private string searchText = "";
    private HashSet<int> pickerChecked = new();

    // “其它添加方式”持久输入
    private string nameInput = "";
    private int indexInput = -1;
    private string bulkInput = "";

    // ========= 恢复日志 =========
    private string LogFolder => Path.Combine(saveFolder, "_logs");
    private string LogPath => Path.Combine(LogFolder, $"{(smr ? smr.gameObject.name : "Mesh")}_originalMeshPath.txt");

    // ========= 入口 =========
    [MenuItem("Tools/Blendshape Range Baker")]
    public static void Open() => GetWindow<BlendshapeRangeBakerWindow>("Blendshape Baker");

    private void OnEnable()
    {
        if (searchField == null) searchField = new SearchField();
        minSize = new Vector2(560, 380);
    }

    // ========= GUI =========
    private void OnGUI()
    {
        DrawHeaderPickSmr();

        // 无对象时不显示后续页签
        if (smr == null || srcMesh == null)
            return;

        // 顶部 Toolbar
        EditorGUILayout.Space(4);
        currentTab = (Tab)GUILayout.Toolbar((int)currentTab, tabNames, GUILayout.Height(24));

        // 顶层滚动
        outerScroll = EditorGUILayout.BeginScrollView(outerScroll);

        switch (currentTab)
        {
            case Tab.选择:
                DrawTabPicker();
                break;
            case Tab.设置:
                DrawTabEntries();
                break;
            case Tab.输出:
                DrawTabOutput();
                break;
        }

        EditorGUILayout.EndScrollView();
    }

    // ========= 顶部 - 选择 SMR =========
    private void DrawHeaderPickSmr()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("1. 选择 SkinnedMeshRenderer", EditorStyles.boldLabel);
        var newSmr = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Renderer", smr, typeof(SkinnedMeshRenderer), true);
        if (newSmr != smr)
        {
            smr = newSmr;
            ReloadMeshInfo();
        }

        if (smr && srcMesh)
        {
            EditorGUILayout.LabelField($"Mesh: {srcMesh.name}    BlendShapes: {srcMesh.blendShapeCount}");
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("重新读取 BlendShapes", GUILayout.Width(160)))
                ReloadMeshInfo();

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("定位到所选对象", GUILayout.Width(140)))
                Selection.activeObject = smr.gameObject;
            EditorGUILayout.EndHorizontal();
        }
    }

    // ========= Tab: Picker（可搜索/多选/多种添加） =========
    private void DrawTabPicker()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("2. 选择 BlendShape（可搜索、多选）", EditorStyles.boldLabel);

        // 搜索框
        Rect r = GUILayoutUtility.GetRect(1, 20);
        searchText = searchField.OnGUI(r, searchText);

        // 过滤列表
        var filtered = FilteredIndices(searchText);

        // 操作栏
        EditorGUILayout.BeginHorizontal();
        using (new EditorGUI.DisabledScope(filtered.Count == 0))
        {
            if (GUILayout.Button("全选当前过滤结果", GUILayout.Width(160)))
                foreach (var i in filtered) pickerChecked.Add(i);

            if (GUILayout.Button("清除当前过滤勾选", GUILayout.Width(160)))
                foreach (var i in filtered) pickerChecked.Remove(i);
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("将勾选的加入条目", GUILayout.Width(160)))
            AddCheckedToEntries();
        EditorGUILayout.EndHorizontal();

        // 可滚动候选列表（限高）
        int rowHeight = 22;
        int maxShown = Mathf.Min(16, filtered.Count);
        int boxHeight = Mathf.Max(2, maxShown) * rowHeight + 6;

        scrollPicker = EditorGUILayout.BeginScrollView(scrollPicker, GUILayout.Height(boxHeight));
        foreach (var idx in filtered)
        {
            EditorGUILayout.BeginHorizontal();
            bool was = pickerChecked.Contains(idx);
            bool now = EditorGUILayout.ToggleLeft($"{idx:000}  {bsNames[idx]}", was);
            if (now && !was) pickerChecked.Add(idx);
            if (!now && was) pickerChecked.Remove(idx);

            if (GUILayout.Button("加入", GUILayout.Width(60)))
                AddOneEntry(idx);
            EditorGUILayout.EndHorizontal();
        }
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("其它添加方式", EditorStyles.miniBoldLabel);

        // 按名称添加
        EditorGUILayout.BeginHorizontal();
        nameInput = EditorGUILayout.TextField("按名称添加", nameInput);
        if (GUILayout.Button("精确匹配添加", GUILayout.Width(120)))
        {
            int idx = System.Array.FindIndex(bsNames, n => n == nameInput);
            if (idx >= 0) AddOneEntry(idx);
            else ShowNotFound($"未找到完全等于 \"{nameInput}\" 的 BlendShape");
        }
        if (GUILayout.Button("包含匹配添加", GUILayout.Width(120)))
        {
            var matches = Enumerable.Range(0, bsNames.Length)
                                    .Where(i => bsNames[i].IndexOf(nameInput, System.StringComparison.OrdinalIgnoreCase) >= 0)
                                    .ToList();
            if (matches.Count == 0) ShowNotFound($"未找到包含 \"{nameInput}\" 的 BlendShape");
            foreach (var i in matches) AddOneEntry(i);
        }
        EditorGUILayout.EndHorizontal();

        // 按索引添加
        EditorGUILayout.BeginHorizontal();
        indexInput = EditorGUILayout.IntField("按索引添加", indexInput);
        if (GUILayout.Button("添加索引", GUILayout.Width(120)))
        {
            if (indexInput >= 0 && indexInput < bsNames.Length) AddOneEntry(indexInput);
            else ShowNotFound($"索引 {indexInput} 越界（0~{bsNames.Length - 1}）");
        }
        EditorGUILayout.EndHorizontal();

        // 批量粘贴
        EditorGUILayout.LabelField("批量粘贴名称（每行或逗号分隔）：", EditorStyles.miniLabel);
        bulkInput = EditorGUILayout.TextArea(bulkInput, GUILayout.MinHeight(48));
        if (GUILayout.Button("批量添加"))
        {
            var tokens = bulkInput.Split(new[] { '\n', '\r', ',', ';', '\t' }, System.StringSplitOptions.RemoveEmptyEntries)
                                  .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct();
            int added = 0, miss = 0;
            foreach (var t in tokens)
            {
                int idx = System.Array.FindIndex(bsNames, n => n == t);
                if (idx >= 0) { AddOneEntry(idx); added++; }
                else miss++;
            }
            if (miss > 0) ShowNotFound($"有 {miss} 个名称未找到；成功添加 {added} 个。");
        }
    }

    // ========= Tab: Entries（条目/比例设置） =========
    private void DrawTabEntries()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("3. 已添加的条目（为每个条目设置新 100% 的比例）", EditorStyles.boldLabel);

        if (entries.Count == 0)
        {
            EditorGUILayout.HelpBox("还没有条目。请在 选择 页勾选/添加后回到此页设置比例。", MessageType.Info);
            return;
        }

        // 可滚动条目区（限高）
        scrollEntries = EditorGUILayout.BeginScrollView(scrollEntries, GUILayout.MinHeight(140), GUILayout.MaxHeight(420));
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"[{i + 1}]  Index {e.blendshapeIndex:000}  {SafeName(e.blendshapeIndex)}", EditorStyles.boldLabel);
            if (GUILayout.Button("删除", GUILayout.Width(64)))
            {
                entries.RemoveAt(i);
                i--;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                continue;
            }
            EditorGUILayout.EndHorizontal();

            e.newMaxPercent = EditorGUILayout.Slider("新 100% = 旧 (%)", e.newMaxPercent, 0f, 100f);
            float scale = Mathf.Clamp01(e.newMaxPercent / 100f);
            EditorGUILayout.HelpBox($"缩放系数：{scale:0.###}（该 BlendShape 所有帧的 delta 顶点/法线/切线将乘以该系数）", MessageType.None);

            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();

        // 批量快捷设置
        // 批量快捷设置
        EditorGUILayout.Space(6);
        EditorGUILayout.BeginHorizontal();
        GUILayout.Label("批量设定：", GUILayout.Width(70));

        // 用持久字段接收滑杆的返回值
        batchPercent = EditorGUILayout.Slider(batchPercent, 0f, 100f);

        if (GUILayout.Button("应用到所有条目", GUILayout.Width(140)))
        {
            foreach (var e in entries) e.newMaxPercent = batchPercent;
        }
        EditorGUILayout.EndHorizontal();

    }

    // ========= Tab: Output（保存/烘焙/恢复） =========
    private void DrawTabOutput()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.LabelField("4. 输出设置与操作", EditorStyles.boldLabel);

        scrollOutput = EditorGUILayout.BeginScrollView(scrollOutput);

        saveFolder = EditorGUILayout.TextField("保存目录", saveFolder);
        autoAssignToSMR = EditorGUILayout.ToggleLeft("烘焙完成后自动挂回到原对象", autoAssignToSMR);

        using (new EditorGUI.DisabledScope(entries.Count == 0))
        {
            if (GUILayout.Button("开始烘焙（生成新 Mesh）", GUILayout.Height(32)))
                Bake();
        }

        using (new EditorGUI.DisabledScope(!File.Exists(LogPath)))
        {
            if (GUILayout.Button("恢复到原始 Mesh", GUILayout.Height(22)))
                RestoreOriginal();
        }

        EditorGUILayout.Space(8);
        EditorGUILayout.HelpBox(
            "工作原理：复制一份可写 Mesh → 读取所有 BlendShape 帧 → 对选中条目按比例缩放 → ClearBlendShapes() → 逐帧 AddBlendShapeFrame() 重建 → 保存为 .asset → 可选自动挂回。\n" +
            "提示：不修改 FBX；法线/切线同步缩放，避免光照异常；条目的比例仅影响被选中的 BlendShape，其它保持不变。",
            MessageType.Info);

        EditorGUILayout.EndScrollView();
    }

    // ========= 数据/工具 =========
    private void ReloadMeshInfo()
    {
        srcMesh = (smr && smr.sharedMesh) ? smr.sharedMesh : null;
        if (srcMesh != null)
        {
            int n = srcMesh.blendShapeCount;
            bsNames = new string[n];
            for (int i = 0; i < n; i++) bsNames[i] = srcMesh.GetBlendShapeName(i);

            // 清理无效条目并刷新显示名
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].blendshapeIndex < 0 || entries[i].blendshapeIndex >= n) entries.RemoveAt(i);
                else entries[i].displayName = bsNames[entries[i].blendshapeIndex];
            }
        }
        else
        {
            bsNames = new string[0];
            entries.Clear();
        }
    }

    private string SafeName(int index)
    {
        if (srcMesh == null || bsNames == null || bsNames.Length == 0)
            return "(N/A)";
        if (index < 0 || index >= bsNames.Length)
            return "(Invalid)";
        return bsNames[index];
    }

    private List<int> FilteredIndices(string query)
    {
        if (bsNames == null || bsNames.Length == 0) return new List<int>();
        if (string.IsNullOrWhiteSpace(query))
            return Enumerable.Range(0, bsNames.Length).ToList();

        var q = query.Trim();

        // 简单通配符：* → 任意字符
        if (q.Contains("*"))
        {
            string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(q).Replace("\\*", ".*") + "$";
            return Enumerable.Range(0, bsNames.Length)
                .Where(i => System.Text.RegularExpressions.Regex.IsMatch(bsNames[i], pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .ToList();
        }
        // 包含匹配
        return Enumerable.Range(0, bsNames.Length)
            .Where(i => bsNames[i].IndexOf(q, System.StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();
    }

    private void AddCheckedToEntries()
    {
        foreach (var i in pickerChecked.ToList())
            AddOneEntry(i);
        pickerChecked.Clear();
    }

    private void AddOneEntry(int idx)
    {
        if (idx < 0 || idx >= bsNames.Length) return;
        if (entries.Any(e => e.blendshapeIndex == idx)) return; // 去重
        entries.Add(new Entry { blendshapeIndex = idx, displayName = bsNames[idx], newMaxPercent = 65f });
    }

    private void ShowNotFound(string msg)
    {
        EditorUtility.DisplayDialog("未找到", msg, "OK");
    }

    private void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets")) return;

        if (!AssetDatabase.IsValidFolder(saveFolder))
        {
            var parent = Path.GetDirectoryName(saveFolder).Replace("\\", "/");
            var leaf = Path.GetFileName(saveFolder);
            if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent)) parent = "Assets";
            AssetDatabase.CreateFolder(parent, leaf);
        }

        if (!AssetDatabase.IsValidFolder(LogFolder))
        {
            var parent = Path.GetDirectoryName(LogFolder).Replace("\\", "/");
            var leaf = Path.GetFileName(LogFolder);
            if (string.IsNullOrEmpty(parent) || !AssetDatabase.IsValidFolder(parent)) parent = saveFolder;
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }

    private void LogOriginalMeshPath()
    {
        try
        {
            EnsureFolders();
            if (!File.Exists(LogPath))
            {
                string meshAssetPath = AssetDatabase.GetAssetPath(srcMesh);
                File.WriteAllText(LogPath, meshAssetPath ?? string.Empty);
                AssetDatabase.Refresh();
            }
        }
        catch { }
    }

    private void RestoreOriginal()
    {
        try
        {
            string path = File.ReadAllText(LogPath).Trim();
            if (string.IsNullOrEmpty(path))
            {
                EditorUtility.DisplayDialog("恢复失败", "日志未记录原始 Mesh 路径。", "OK");
                return;
            }
            var originalMesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (originalMesh == null)
            {
                EditorUtility.DisplayDialog("恢复失败", $"找不到原始 Mesh 资产：\n{path}", "OK");
                return;
            }
            Undo.RecordObject(smr, "Restore Original Mesh");
            smr.sharedMesh = originalMesh;
            srcMesh = originalMesh;
            ReloadMeshInfo();
            EditorUtility.DisplayDialog("完成", "已恢复到原始 Mesh。", "OK");
        }
        catch (System.Exception ex)
        {
            EditorUtility.DisplayDialog("错误", ex.Message, "OK");
        }
    }

    // ========= 核心烘焙 =========
    private void Bake()
    {
        if (smr == null || srcMesh == null || entries.Count == 0) return;

        // 聚合 index → scale（重复取最后一次）
        var scaleMap = new Dictionary<int, float>();
        foreach (var e in entries)
        {
            if (e.blendshapeIndex < 0 || e.blendshapeIndex >= srcMesh.blendShapeCount) continue;
            scaleMap[e.blendshapeIndex] = Mathf.Clamp01(e.newMaxPercent / 100f);
        }
        if (scaleMap.Count == 0)
        {
            EditorUtility.DisplayDialog("提示", "没有有效条目。", "OK");
            return;
        }

        // 复制 Mesh
        var dst = Object.Instantiate(srcMesh);
        dst.name = srcMesh.name + "_Edited";

        int bsCount = dst.blendShapeCount;
        int vertexCount = dst.vertexCount;

        var cached = new List<(string name, List<(float w, Vector3[] dv, Vector3[] dn, Vector3[] dt)>)>(bsCount);

        // 缓存并按需缩放
        for (int i = 0; i < bsCount; i++)
        {
            string name = dst.GetBlendShapeName(i);
            int frameCount = dst.GetBlendShapeFrameCount(i);
            var frames = new List<(float, Vector3[], Vector3[], Vector3[])>(frameCount);

            bool doScale = scaleMap.TryGetValue(i, out float scale);

            for (int f = 0; f < frameCount; f++)
            {
                float w = dst.GetBlendShapeFrameWeight(i, f);
                var dv = new Vector3[vertexCount];
                var dn = new Vector3[vertexCount];
                var dt = new Vector3[vertexCount];
                dst.GetBlendShapeFrameVertices(i, f, dv, dn, dt);

                if (doScale)
                {
                    for (int k = 0; k < vertexCount; k++)
                    {
                        dv[k] *= scale;
                        dn[k] *= scale;
                        dt[k] *= scale;
                    }
                }
                frames.Add((w, dv, dn, dt));
            }
            cached.Add((name, frames));
        }

        // 重建帧
        dst.ClearBlendShapes();
        foreach (var (name, frames) in cached)
            foreach (var (w, dv, dn, dt) in frames)
                dst.AddBlendShapeFrame(name, w, dv, dn, dt);

        // 保存资产
        EnsureFolders();
        string assetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(saveFolder, dst.name + ".asset"));
        AssetDatabase.CreateAsset(dst, assetPath);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        // 记录原始 Mesh 路径
        LogOriginalMeshPath();

        // 自动挂回
        if (autoAssignToSMR)
        {
            Undo.RecordObject(smr, "Assign Edited Mesh");
            smr.sharedMesh = dst;
        }

        // 刷新
        srcMesh = dst;
        ReloadMeshInfo();

        var summary = string.Join(", ", scaleMap.Keys.Select(k => $"{bsNames[k]}={scaleMap[k]*100f:0.#}%"));
        EditorUtility.DisplayDialog("完成", $"已生成：{assetPath}\n缩放：{summary}", "OK");
    }
}
#endif
