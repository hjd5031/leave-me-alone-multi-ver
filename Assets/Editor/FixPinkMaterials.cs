using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.Rendering.Universal;
using UnityEditor.Rendering.Universal.ShaderGUI;
using UnityEngine;
using UnityEngine.Rendering;

public sealed class FixPinkMaterials : EditorWindow
{
    private const string UrpLitShaderName = "Universal Render Pipeline/Lit";
    private const string UrpUnlitShaderName = "Universal Render Pipeline/Unlit";
    private const string InternalErrorShaderName = "Hidden/InternalErrorShader";

    private enum RepairKind
    {
        OfficialUpgrade,
        Fallback,
        Manual
    }

    private enum FallbackShader
    {
        Lit,
        Unlit
    }

    [Serializable]
    private sealed class ScanResult
    {
        public Material Material;
        public string Path;
        public string ShaderName;
        public string Description;
        public RepairKind Kind;
        public bool Selected;
    }

    private struct SavedTexture
    {
        public Texture Texture;
        public Vector2 Scale;
        public Vector2 Offset;
    }

    private readonly List<ScanResult> _results = new List<ScanResult>();
    private List<MaterialUpgrader> _officialUpgraders = new List<MaterialUpgrader>();
    private Dictionary<string, MaterialUpgrader> _upgraderByShader =
        new Dictionary<string, MaterialUpgrader>(StringComparer.Ordinal);

    private Vector2 _scrollPosition;
    private FallbackShader _fallbackShader = FallbackShader.Lit;
    private string _status = "Scan the project or selected folders to find materials that need attention.";

    [MenuItem("Tools/Rendering/Pink Material Repair Tool")]
    public static void OpenWindow()
    {
        FixPinkMaterials window = GetWindow<FixPinkMaterials>("Pink Material Repair");
        window.minSize = new Vector2(760f, 420f);
        window.Show();
    }

    [MenuItem("Tools/Fix Pink Materials")]
    private static void OpenLegacyMenu()
    {
        OpenWindow();
    }

    private void OnEnable()
    {
        LoadOfficialUpgraders();
    }

    private void OnGUI()
    {
        DrawHeader();
        DrawToolbar();
        DrawSummary();
        DrawResults();
        DrawFooter();
    }

    private void DrawHeader()
    {
        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("URP Pink Material Repair Tool", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(
            "Uses Unity's official URP material upgraders when possible. Unknown or missing shaders require an explicit fallback.",
            EditorStyles.wordWrappedLabel);

        RenderPipelineAsset pipeline = GraphicsSettings.defaultRenderPipeline;
        bool urpIsActive = pipeline != null &&
                           pipeline.GetType().Name.IndexOf(
                               "UniversalRenderPipelineAsset",
                               StringComparison.OrdinalIgnoreCase) >= 0;

        if (!urpIsActive)
        {
            EditorGUILayout.HelpBox(
                "URP is not the active default render pipeline. Assign a Universal Render Pipeline Asset in Project Settings > Graphics before repairing materials.",
                MessageType.Warning);
        }

        EditorGUILayout.HelpBox(
            "Commit or back up material files before repairing them. Official upgrades are selected automatically; fallback repairs are opt-in because custom shader behavior cannot be reconstructed reliably.",
            MessageType.Info);
    }

    private void DrawToolbar()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

        if (GUILayout.Button("Scan Project", EditorStyles.toolbarButton, GUILayout.Width(100f)))
        {
            ScanProject();
        }

        if (GUILayout.Button("Scan Selection", EditorStyles.toolbarButton, GUILayout.Width(105f)))
        {
            ScanSelection();
        }

        GUILayout.Space(12f);

        if (GUILayout.Button("Select Safe", EditorStyles.toolbarButton, GUILayout.Width(85f)))
        {
            SetSelection(result => result.Kind == RepairKind.OfficialUpgrade);
        }

        if (GUILayout.Button("Select Repairable", EditorStyles.toolbarButton, GUILayout.Width(110f)))
        {
            SetSelection(result => result.Kind != RepairKind.Manual);
        }

        if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(55f)))
        {
            SetSelection(result => false);
        }

        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
    }

    private void DrawSummary()
    {
        int officialCount = _results.Count(result => result.Kind == RepairKind.OfficialUpgrade);
        int fallbackCount = _results.Count(result => result.Kind == RepairKind.Fallback);
        int manualCount = _results.Count(result => result.Kind == RepairKind.Manual);
        int selectedCount = _results.Count(result => result.Selected);

        EditorGUILayout.Space(5f);
        EditorGUILayout.LabelField(
            $"Found {_results.Count} | Official {officialCount} | Fallback {fallbackCount} | Manual {manualCount} | Selected {selectedCount}",
            EditorStyles.miniBoldLabel);
        EditorGUILayout.LabelField(_status, EditorStyles.wordWrappedMiniLabel);
    }

    private void DrawResults()
    {
        EditorGUILayout.Space(4f);
        _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);

        if (_results.Count == 0)
        {
            EditorGUILayout.HelpBox("No scan results.", MessageType.None);
        }

        foreach (ScanResult result in _results)
        {
            DrawResult(result);
        }

        EditorGUILayout.EndScrollView();
    }

    private static void DrawResult(ScanResult result)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();

        using (new EditorGUI.DisabledScope(result.Kind == RepairKind.Manual))
        {
            result.Selected = EditorGUILayout.Toggle(result.Selected, GUILayout.Width(18f));
        }

        EditorGUILayout.ObjectField(result.Material, typeof(Material), false);

        string badge = result.Kind == RepairKind.OfficialUpgrade
            ? "OFFICIAL"
            : result.Kind == RepairKind.Fallback
                ? "FALLBACK"
                : "MANUAL";
        GUILayout.Label(badge, EditorStyles.miniBoldLabel, GUILayout.Width(65f));

        if (GUILayout.Button("Ping", GUILayout.Width(42f)))
        {
            EditorGUIUtility.PingObject(result.Material);
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField($"Shader: {result.ShaderName}", EditorStyles.miniLabel);
        EditorGUILayout.LabelField(result.Description, EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.SelectableLabel(result.Path, EditorStyles.miniLabel, GUILayout.Height(16f));
        EditorGUILayout.EndVertical();
    }

    private void DrawFooter()
    {
        EditorGUILayout.Space(5f);
        _fallbackShader = (FallbackShader)EditorGUILayout.EnumPopup(
            new GUIContent(
                "Fallback Shader",
                "Used only for checked FALLBACK entries. Lit is appropriate for most 3D environment materials; Unlit ignores scene lighting."),
            _fallbackShader);

        int selectedCount = _results.Count(result => result.Selected);
        using (new EditorGUI.DisabledScope(selectedCount == 0))
        {
            if (GUILayout.Button($"Repair {selectedCount} Selected Material(s)", GUILayout.Height(30f)))
            {
                RepairSelected();
            }
        }

        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Open Unity Render Pipeline Converter", GUILayout.Width(240f)))
        {
            EditorApplication.ExecuteMenuItem("Window/Rendering/Render Pipeline Converter");
        }
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(6f);
    }

    private void ScanProject()
    {
        string[] guids = AssetDatabase.FindAssets("t:Material", new[] { "Assets" });
        ScanPaths(guids.Select(AssetDatabase.GUIDToAssetPath));
        _status = $"Scanned {guids.Length} project material assets.";
    }

    private void ScanSelection()
    {
        HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string> folders = new List<string>();

        foreach (UnityEngine.Object selectedObject in Selection.objects)
        {
            string path = AssetDatabase.GetAssetPath(selectedObject);
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            if (AssetDatabase.IsValidFolder(path))
            {
                folders.Add(path);
            }
            else if (selectedObject is Material)
            {
                paths.Add(path);
            }
        }

        if (folders.Count > 0)
        {
            string[] folderMaterials = AssetDatabase.FindAssets("t:Material", folders.ToArray());
            foreach (string guid in folderMaterials)
            {
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));
            }
        }

        ScanPaths(paths);
        _status = paths.Count == 0
            ? "Select one or more material assets or folders in the Project window."
            : $"Scanned {paths.Count} selected material assets.";
    }

    private void ScanPaths(IEnumerable<string> paths)
    {
        LoadOfficialUpgraders();
        _results.Clear();

        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            ScanResult result = AnalyzeMaterial(material, path);
            if (result != null)
            {
                _results.Add(result);
            }
        }

        _results.Sort((left, right) =>
        {
            int kindComparison = left.Kind.CompareTo(right.Kind);
            return kindComparison != 0
                ? kindComparison
                : string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
        });

        Repaint();
    }

    private ScanResult AnalyzeMaterial(Material material, string path)
    {
        if (material == null || material.isVariant)
        {
            return null;
        }

        Shader shader = material.shader;
        string shaderName = shader != null ? shader.name : "<missing>";

        MaterialUpgrader upgrader;
        if (_upgraderByShader.TryGetValue(shaderName, out upgrader))
        {
            return new ScanResult
            {
                Material = material,
                Path = path,
                ShaderName = shaderName,
                Description = $"Unity URP upgrader will convert this material to {upgrader.NewShaderPath}.",
                Kind = RepairKind.OfficialUpgrade,
                Selected = true
            };
        }

        bool missingOrError = shader == null ||
                              string.Equals(shaderName, InternalErrorShaderName, StringComparison.Ordinal);
        if (missingOrError)
        {
            return new ScanResult
            {
                Material = material,
                Path = path,
                ShaderName = shaderName,
                Description = "The original shader is missing. Common textures and values can be copied to the selected URP fallback shader, but custom behavior will be lost.",
                Kind = RepairKind.Fallback,
                Selected = false
            };
        }

        if (!shader.isSupported)
        {
            bool isUrpShader = IsUrpShader(material);
            return new ScanResult
            {
                Material = material,
                Path = path,
                ShaderName = shaderName,
                Description = isUrpShader
                    ? "This is already a URP shader but it is unsupported or failed to compile. Repair the shader or Shader Graph rather than replacing the material."
                    : "This custom shader is unsupported in the active pipeline. A fallback can preserve common material properties but cannot reproduce custom shader behavior.",
                Kind = isUrpShader ? RepairKind.Manual : RepairKind.Fallback,
                Selected = false
            };
        }

        return null;
    }

    private static bool IsUrpShader(Material material)
    {
        Shader shader = material != null ? material.shader : null;
        if (shader == null)
        {
            return false;
        }

        if (shader.name.StartsWith("Universal Render Pipeline/", StringComparison.Ordinal))
        {
            return true;
        }

        string pipelineTag = material.GetTag("RenderPipeline", false, string.Empty);
        return pipelineTag.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void SetSelection(Func<ScanResult, bool> selector)
    {
        foreach (ScanResult result in _results)
        {
            result.Selected = result.Kind != RepairKind.Manual && selector(result);
        }

        Repaint();
    }

    private void RepairSelected()
    {
        List<ScanResult> selectedResults =
            _results.Where(result => result.Selected && result.Kind != RepairKind.Manual).ToList();
        if (selectedResults.Count == 0)
        {
            return;
        }

        int fallbackCount = selectedResults.Count(result => result.Kind == RepairKind.Fallback);
        string fallbackWarning = fallbackCount > 0
            ? $"\n\n{fallbackCount} fallback repair(s) may lose custom shader behavior."
            : string.Empty;

        bool confirmed = EditorUtility.DisplayDialog(
            "Repair URP Materials",
            $"This will overwrite {selectedResults.Count} material asset(s). Make sure the changes are committed or backed up.{fallbackWarning}",
            "Repair",
            "Cancel");
        if (!confirmed)
        {
            return;
        }

        int repairedCount = 0;
        int failedCount = 0;

        try
        {
            for (int index = 0; index < selectedResults.Count; index++)
            {
                ScanResult result = selectedResults[index];
                bool cancelled = EditorUtility.DisplayCancelableProgressBar(
                    "Repairing URP Materials",
                    result.Path,
                    (float)index / selectedResults.Count);
                if (cancelled)
                {
                    break;
                }

                Undo.RecordObject(result.Material, "Repair URP Material");
                bool repaired = result.Kind == RepairKind.OfficialUpgrade
                    ? ApplyOfficialUpgrade(result.Material)
                    : ApplyFallback(result.Material);

                if (repaired)
                {
                    EditorUtility.SetDirty(result.Material);
                    repairedCount++;
                }
                else
                {
                    failedCount++;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        _status = $"Repair complete. Repaired {repairedCount}; failed {failedCount}.";

        List<string> rescannedPaths = _results.Select(result => result.Path).ToList();
        ScanPaths(rescannedPaths);
        _status = $"Repair complete. Repaired {repairedCount}; failed {failedCount}; remaining {_results.Count}.";

        Debug.Log($"[PinkMaterialRepair] Repaired {repairedCount} material(s); failed {failedCount}; remaining {_results.Count}.");
    }

    private bool ApplyOfficialUpgrade(Material material)
    {
        string message = string.Empty;
        bool upgraded = MaterialUpgrader.Upgrade(
            material,
            _officialUpgraders,
            MaterialUpgrader.UpgradeFlags.LogMessageWhenNoUpgraderFound,
            ref message);

        if (!upgraded && !string.IsNullOrEmpty(message))
        {
            Debug.LogWarning($"[PinkMaterialRepair] {message}", material);
        }

        return upgraded;
    }

    private bool ApplyFallback(Material material)
    {
        string shaderName = _fallbackShader == FallbackShader.Lit
            ? UrpLitShaderName
            : UrpUnlitShaderName;
        Shader targetShader = Shader.Find(shaderName);
        if (targetShader == null)
        {
            Debug.LogError($"[PinkMaterialRepair] Could not find fallback shader '{shaderName}'.", material);
            return false;
        }

        SavedTexture baseMap;
        SavedTexture normalMap;
        SavedTexture occlusionMap;
        SavedTexture emissionMap;
        Color baseColor;
        Color emissionColor;
        float metallic;
        float smoothness;
        float bumpScale;
        float cutoff;
        float surface;
        float legacyMode;

        bool hasBaseMap = TryGetSavedTexture(material, new[] { "_BaseMap", "_MainTex" }, out baseMap);
        bool hasNormalMap = TryGetSavedTexture(material, new[] { "_BumpMap", "_NormalMap" }, out normalMap);
        bool hasOcclusionMap = TryGetSavedTexture(material, new[] { "_OcclusionMap" }, out occlusionMap);
        bool hasEmissionMap = TryGetSavedTexture(material, new[] { "_EmissionMap" }, out emissionMap);
        bool hasBaseColor = TryGetSavedColor(material, new[] { "_BaseColor", "_Color" }, out baseColor);
        bool hasEmissionColor = TryGetSavedColor(material, new[] { "_EmissionColor" }, out emissionColor);
        bool hasMetallic = TryGetSavedFloat(material, new[] { "_Metallic" }, out metallic);
        bool hasSmoothness = TryGetSavedFloat(material, new[] { "_Smoothness", "_Glossiness" }, out smoothness);
        bool hasBumpScale = TryGetSavedFloat(material, new[] { "_BumpScale" }, out bumpScale);
        bool hasCutoff = TryGetSavedFloat(material, new[] { "_Cutoff" }, out cutoff);
        bool hasSurface = TryGetSavedFloat(material, new[] { "_Surface" }, out surface);
        bool hasLegacyMode = TryGetSavedFloat(material, new[] { "_Mode" }, out legacyMode);

        bool transparent = (hasSurface && surface > 0.5f) ||
                           (hasLegacyMode && legacyMode >= 2f) ||
                           material.renderQueue >= (int)RenderQueue.Transparent;
        bool alphaClip = (hasLegacyMode && Mathf.Approximately(legacyMode, 1f)) ||
                         material.IsKeywordEnabled("_ALPHATEST_ON");

        material.shader = targetShader;

        if (hasBaseMap)
        {
            ApplyTexture(material, "_BaseMap", baseMap);
        }

        if (hasBaseColor && material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", baseColor);
        }

        if (_fallbackShader == FallbackShader.Lit)
        {
            if (hasNormalMap)
            {
                ApplyTexture(material, "_BumpMap", normalMap);
            }

            if (hasOcclusionMap)
            {
                ApplyTexture(material, "_OcclusionMap", occlusionMap);
            }

            if (hasMetallic && material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", metallic);
            }

            if (hasSmoothness && material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", smoothness);
            }

            if (hasBumpScale && material.HasProperty("_BumpScale"))
            {
                material.SetFloat("_BumpScale", bumpScale);
            }
        }

        if (hasEmissionMap)
        {
            ApplyTexture(material, "_EmissionMap", emissionMap);
        }

        if (hasEmissionColor && material.HasProperty("_EmissionColor"))
        {
            material.SetColor("_EmissionColor", emissionColor);
        }

        if (hasCutoff && material.HasProperty("_Cutoff"))
        {
            material.SetFloat("_Cutoff", cutoff);
        }

        ConfigureSurface(material, transparent, alphaClip);
        if (_fallbackShader == FallbackShader.Lit)
        {
            BaseShaderGUI.SetMaterialKeywords(material, LitGUI.SetMaterialKeywords);
        }
        else
        {
            BaseShaderGUI.SetMaterialKeywords(material);
        }
        return true;
    }

    private static void ConfigureSurface(Material material, bool transparent, bool alphaClip)
    {
        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", transparent ? 1f : 0f);
        }

        if (material.HasProperty("_AlphaClip"))
        {
            material.SetFloat("_AlphaClip", alphaClip ? 1f : 0f);
        }

        if (transparent)
        {
            material.SetOverrideTag("RenderType", "Transparent");
            SetFloatIfPresent(material, "_SrcBlend", (float)BlendMode.SrcAlpha);
            SetFloatIfPresent(material, "_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            SetFloatIfPresent(material, "_ZWrite", 0f);
            material.renderQueue = (int)RenderQueue.Transparent;
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        else
        {
            material.SetOverrideTag("RenderType", alphaClip ? "TransparentCutout" : "Opaque");
            SetFloatIfPresent(material, "_SrcBlend", (float)BlendMode.One);
            SetFloatIfPresent(material, "_DstBlend", (float)BlendMode.Zero);
            SetFloatIfPresent(material, "_ZWrite", 1f);
            material.renderQueue = alphaClip
                ? (int)RenderQueue.AlphaTest
                : (int)RenderQueue.Geometry;
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        if (alphaClip)
        {
            material.EnableKeyword("_ALPHATEST_ON");
        }
        else
        {
            material.DisableKeyword("_ALPHATEST_ON");
        }
    }

    private static void SetFloatIfPresent(Material material, string propertyName, float value)
    {
        if (material.HasProperty(propertyName))
        {
            material.SetFloat(propertyName, value);
        }
    }

    private static void ApplyTexture(Material material, string propertyName, SavedTexture savedTexture)
    {
        if (!material.HasProperty(propertyName))
        {
            return;
        }

        material.SetTexture(propertyName, savedTexture.Texture);
        material.SetTextureScale(propertyName, savedTexture.Scale);
        material.SetTextureOffset(propertyName, savedTexture.Offset);
    }

    private static bool TryGetSavedTexture(
        Material material,
        IEnumerable<string> names,
        out SavedTexture value)
    {
        SerializedProperty array = new SerializedObject(material)
            .FindProperty("m_SavedProperties.m_TexEnvs");
        if (array != null && array.isArray)
        {
            foreach (string name in names)
            {
                for (int index = 0; index < array.arraySize; index++)
                {
                    SerializedProperty pair = array.GetArrayElementAtIndex(index);
                    if (pair.FindPropertyRelative("first").stringValue != name)
                    {
                        continue;
                    }

                    SerializedProperty textureValue = pair.FindPropertyRelative("second");
                    value = new SavedTexture
                    {
                        Texture = textureValue.FindPropertyRelative("m_Texture").objectReferenceValue as Texture,
                        Scale = textureValue.FindPropertyRelative("m_Scale").vector2Value,
                        Offset = textureValue.FindPropertyRelative("m_Offset").vector2Value
                    };
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetSavedFloat(
        Material material,
        IEnumerable<string> names,
        out float value)
    {
        SerializedProperty array = new SerializedObject(material)
            .FindProperty("m_SavedProperties.m_Floats");
        if (array != null && array.isArray)
        {
            foreach (string name in names)
            {
                for (int index = 0; index < array.arraySize; index++)
                {
                    SerializedProperty pair = array.GetArrayElementAtIndex(index);
                    if (pair.FindPropertyRelative("first").stringValue == name)
                    {
                        value = pair.FindPropertyRelative("second").floatValue;
                        return true;
                    }
                }
            }
        }

        value = 0f;
        return false;
    }

    private static bool TryGetSavedColor(
        Material material,
        IEnumerable<string> names,
        out Color value)
    {
        SerializedProperty array = new SerializedObject(material)
            .FindProperty("m_SavedProperties.m_Colors");
        if (array != null && array.isArray)
        {
            foreach (string name in names)
            {
                for (int index = 0; index < array.arraySize; index++)
                {
                    SerializedProperty pair = array.GetArrayElementAtIndex(index);
                    if (pair.FindPropertyRelative("first").stringValue == name)
                    {
                        value = pair.FindPropertyRelative("second").colorValue;
                        return true;
                    }
                }
            }
        }

        value = Color.white;
        return false;
    }

    private void LoadOfficialUpgraders()
    {
        _officialUpgraders = CreateOfficialUpgraderList();
        _upgraderByShader = BuildUpgraderLookup(_officialUpgraders);
    }

    private static List<MaterialUpgrader> CreateOfficialUpgraderList()
    {
        List<MaterialUpgrader> upgraders = new List<MaterialUpgrader>();

        try
        {
            Assembly urpEditorAssembly = typeof(StandardUpgrader).Assembly;
            Type converterType = urpEditorAssembly.GetType(
                "UnityEditor.Rendering.Universal.UniversalRenderPipelineMaterialUpgrader");
            object converter = converterType != null
                ? Activator.CreateInstance(converterType, true)
                : null;
            PropertyInfo property = converterType?.GetProperty(
                "upgraders",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            IEnumerable values = property?.GetValue(converter) as IEnumerable;

            if (values != null)
            {
                foreach (object value in values)
                {
                    if (value is MaterialUpgrader upgrader)
                    {
                        upgraders.Add(upgrader);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning(
                $"[PinkMaterialRepair] Could not load Unity's complete URP upgrader list: {exception.Message}");
        }

        if (upgraders.Count == 0)
        {
            upgraders.Add(new StandardUpgrader("Standard"));
            upgraders.Add(new StandardUpgrader("Standard (Specular setup)"));
            upgraders.Add(new TerrainUpgrader("Nature/Terrain/Standard"));
            upgraders.Add(new ParticleUpgrader("Particles/Standard Surface"));
            upgraders.Add(new ParticleUpgrader("Particles/Standard Unlit"));
            upgraders.Add(new AutodeskInteractiveUpgrader("Autodesk Interactive"));
        }

        return upgraders;
    }

    private static Dictionary<string, MaterialUpgrader> BuildUpgraderLookup(
        IEnumerable<MaterialUpgrader> upgraders)
    {
        Dictionary<string, MaterialUpgrader> lookup =
            new Dictionary<string, MaterialUpgrader>(StringComparer.Ordinal);
        FieldInfo oldShaderField = typeof(MaterialUpgrader).GetField(
            "m_OldShader",
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (oldShaderField == null)
        {
            return lookup;
        }

        foreach (MaterialUpgrader upgrader in upgraders)
        {
            string oldShaderName = oldShaderField.GetValue(upgrader) as string;
            if (!string.IsNullOrEmpty(oldShaderName))
            {
                lookup[oldShaderName] = upgrader;
            }
        }

        return lookup;
    }
}
