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
            EnsureInputSystem();
            EnsureMaterial("Assets/Resources/Materials/Ground.mat", "Universal Render Pipeline/Lit", new Color(0.36f, 0.42f, 0.33f));
            EnsureMaterial("Assets/Resources/Materials/Flame.mat", "Universal Render Pipeline/Unlit", new Color(1f, 0.63f, 0.13f));
            EnsureParticleMaterial("Assets/Resources/Materials/ParticleAdd.mat", true);      // ball touch sparks
            EnsureParticleMaterial("Assets/Resources/Materials/ParticleAlpha.mat", false);   // boost trail
            EnsureBallMaterial("Assets/Resources/Materials/Ball.mat");
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

        /// <summary>
        /// Gamepad sticks / triggers need the Input System package to be the active input handler (Player settings: "Active Input Handling").
        /// 1 = Input System package only. Unity applies a changed value after an Editor restart; the runtime code falls back to the legacy
        /// Input Manager (keyboard + gamepad buttons only) until then.
        /// </summary>
        static void EnsureInputSystem()
        {
            var all = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (all == null || all.Length == 0) return;
            var so = new SerializedObject(all[0]);
            var p = so.FindProperty("activeInputHandler");
            if (p == null || p.intValue == 1) return;
            p.intValue = 1;
            so.ApplyModifiedPropertiesWithoutUndo();
            Debug.Log("[RocketWorld] Active Input Handling set to Input System Package (restart the Editor if prompted).");
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

        /// <summary>URP Particles/Unlit, transparent, additive or alpha blended. The shader + keywords live in the asset, so they survive WebGL stripping.</summary>
        static void EnsureParticleMaterial(string path, bool additive)
        {
            var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (existing != null) return;
            var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (sh == null) { Debug.LogWarning("[RocketWorld] shader not found: Universal Render Pipeline/Particles/Unlit"); return; }
            var m = new Material(sh);
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);                                   // transparent
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", additive ? 2f : 0f);                       // 0 alpha, 2 additive
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", additive ? (float)UnityEngine.Rendering.BlendMode.One : (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)UnityEngine.Rendering.BlendMode.One);
            if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", additive ? (float)UnityEngine.Rendering.BlendMode.One : (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            if (m.HasProperty("_SoftParticlesEnabled")) m.SetFloat("_SoftParticlesEnabled", 0f);
            if (m.HasProperty("_CameraFadingEnabled")) m.SetFloat("_CameraFadingEnabled", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.SetOverrideTag("RenderType", "Transparent");
            m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            AssetDatabase.CreateAsset(m, path);
        }

        /// <summary>URP Lit with the emission keyword on (the ball is lit by its own texture, like the web version's emissive 0.9).</summary>
        static void EnsureBallMaterial(string path)
        {
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh == null) { Debug.LogWarning("[RocketWorld] shader not found: Universal Render Pipeline/Lit"); return; }
            var m = new Material(sh);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.35f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            m.EnableKeyword("_EMISSION");
            if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.white * 0.9f);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
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
