using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RocketWorld.Editor
{
    /// <summary>
    /// Creates everything that normally lives in serialized assets (URP asset, a few materials, the empty boot scene) so the
    /// repository stays code-only and CI can build from a clean checkout. Idempotent; runs on every Editor load and before builds.
    /// </summary>
    public static class ProjectSetup
    {
        public const string ScenePath = "Assets/Scenes/Main.unity";
        const string RpPath = "Assets/Settings/RocketWorld_URP.asset";
        const string RendererPath = "Assets/Settings/RocketWorld_Renderer.asset";

        [InitializeOnLoadMethod]
        static void OnLoad() { EditorApplication.delayCall += () => { if (!Application.isPlaying) EnsureAll(); }; }

        [MenuItem("RocketWorld/Ensure project setup")]
        public static void EnsureAll()
        {
            Directory.CreateDirectory("Assets/Settings"); Directory.CreateDirectory("Assets/Scenes"); Directory.CreateDirectory("Assets/Resources/Materials");
            EnsureUrp();
            EnsureMaterial("Assets/Resources/Materials/Ground.mat", "Universal Render Pipeline/Lit", new Color(0.36f, 0.42f, 0.33f));
            EnsureMaterial("Assets/Resources/Materials/Sky.mat", "Skybox/Procedural", Color.white);
            EnsureScene();
            AssetDatabase.SaveAssets();
        }

        static void EnsureUrp()
        {
            var rp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(RpPath);
            if (rp == null)
            {
                var data = ScriptableObject.CreateInstance<UniversalRendererData>();
                AssetDatabase.CreateAsset(data, RendererPath);
                rp = UniversalRenderPipelineAsset.Create(data);
                AssetDatabase.CreateAsset(rp, RpPath);
                // Lean defaults: tiles carry baked lighting, so no shadows/HDR/MSAA (SMAA is on the camera).
                var so = new SerializedObject(rp);
                SetBool(so, "m_SupportsHDR", false); SetBool(so, "m_MainLightShadowsSupported", false); SetBool(so, "m_AdditionalLightShadowsSupported", false);
                SetBool(so, "m_SupportsTerrainHoles", false); SetBool(so, "m_SupportsDynamicBatching", false);
                var msaa = so.FindProperty("m_MSAA"); if (msaa != null) msaa.intValue = 1;
                var rs = so.FindProperty("m_RenderScale"); if (rs != null) rs.floatValue = 1f;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(rp);
            }
            if (GraphicsSettings.defaultRenderPipeline != rp) GraphicsSettings.defaultRenderPipeline = rp;
            int current = QualitySettings.GetQualityLevel();
            for (int i = 0; i < QualitySettings.names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                if (QualitySettings.renderPipeline != rp) QualitySettings.renderPipeline = rp;
            }
            QualitySettings.SetQualityLevel(current, false);
            PlayerSettings.colorSpace = ColorSpace.Linear;
        }

        static void SetBool(SerializedObject so, string name, bool v) { var p = so.FindProperty(name); if (p != null) p.boolValue = v; }

        static void EnsureMaterial(string path, string shaderName, Color color)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            var sh = Shader.Find(shaderName);
            if (sh == null) { Debug.LogWarning("[RocketWorld] shader not found: " + shaderName); return; }
            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color); else if (m.HasProperty("_Color")) m.SetColor("_Color", color);
            AssetDatabase.CreateAsset(m, path);
        }

        static void EnsureScene()
        {
            if (!File.Exists(ScenePath))
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, ScenePath);
            }
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        }
    }
}
