using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace RocketWorld.Editor
{
    /// <summary>Headless entry point used by CI (game-ci: -buildMethod RocketWorld.Editor.BuildScript.BuildWebGL) and the Build menu.</summary>
    public static class BuildScript
    {
        [MenuItem("RocketWorld/Build WebGL")]
        public static void BuildWebGL()
        {
            ProjectSetup.EnsureAll();
            EnsureNetworkAssets();
            ConfigureWebGL();
            string path = Arg("-customBuildPath") ?? "build/WebGL";
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ProjectSetup.ScenePath },
                locationPathName = path,
                target = BuildTarget.WebGL,
                options = BuildOptions.None,
            });
            if (report.summary.result == BuildResult.Succeeded) PostProcessIndex(path);
            Debug.Log($"[RocketWorld] build {report.summary.result}, {report.summary.totalSize / (1024 * 1024)} MB -> {path}");
            if (report.summary.result != BuildResult.Succeeded && Application.isBatchMode) EditorApplication.Exit(1);
        }

        // The multiplayer prefab is created by Assets/Net/Editor/NetPrefabSetup (another assembly): call it by reflection.
        static void EnsureNetworkAssets()
        {
            try
            {
                var t = Type.GetType("RocketWorld.Net.Editor.NetPrefabSetup, Assembly-CSharp-Editor");
                t?.GetMethod("EnsureAll")?.Invoke(null, null);
                AssetDatabase.Refresh();
                // Unity Cloud project id for Relay/Lobby. Unity Services read it from Application.cloudProjectId in the player, which CI
                // (no linked project) leaves empty -> UnityProjectNotLinkedException. Sources: -rwCloudProject, env UNITY_CLOUD_PROJECT_ID,
                // or a committed Assets/Resources/Net/cloud_project.txt (the id is not a secret: every player build carries it).
                const string idFile = "Assets/Resources/Net/cloud_project.txt";
                var cloud = (Arg("-rwCloudProject") ?? Environment.GetEnvironmentVariable("UNITY_CLOUD_PROJECT_ID") ?? "").Trim();
                if (string.IsNullOrEmpty(cloud) && System.IO.File.Exists(idFile)) cloud = System.IO.File.ReadAllText(idFile).Trim();
                if (!string.IsNullOrEmpty(cloud))
                {
                    System.IO.Directory.CreateDirectory("Assets/Resources/Net");
                    System.IO.File.WriteAllText(idFile, cloud);
                    ApplyCloudProjectId(cloud);
                }
                else Debug.LogWarning("[RocketWorld] no Unity Cloud project id (UNITY_CLOUD_PROJECT_ID / Assets/Resources/Net/cloud_project.txt): multiplayer will fail with UnityProjectNotLinkedException");
            }
            catch (Exception e) { Debug.LogWarning("[RocketWorld] multiplayer setup failed: " + e.Message); }
        }

        static void ApplyCloudProjectId(string id)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
            if (assets == null || assets.Length == 0) { Debug.LogWarning("[RocketWorld] PlayerSettings asset not found: cannot set the cloud project id"); return; }
            var so = new SerializedObject(assets[0]);
            var p = so.FindProperty("cloudProjectId");
            if (p == null) { Debug.LogWarning("[RocketWorld] PlayerSettings has no cloudProjectId property"); return; }
            p.stringValue = id;
            foreach (var n in new[] { "cloudEnabled", "cloudServicesEnabled" }) { var en = so.FindProperty(n); if (en != null && en.propertyType == SerializedPropertyType.Boolean) en.boolValue = true; }
            so.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            var back = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]).FindProperty("cloudProjectId");
            Debug.Log("[RocketWorld] cloud project id set for this build: " + id + " (read back: " + (back != null ? back.stringValue : "?") + ")");
        }

        // Bakes ION_TOKEN (from env / -rwIonToken) into window.RW_DEFAULT_ION_TOKEN for Cesium ion tiles.
        static void PostProcessIndex(string dir)
        {
            try
            {
                var file = System.IO.Path.Combine(dir, "index.html");
                var html = System.IO.File.ReadAllText(file);
                const string marker = "<!-- rocketworld-checks -->";
                if (html.Contains(marker) || !html.Contains("</body>")) return;

                // Prefer explicit build arg, then the ION_TOKEN GitHub Actions secret (passed as env to game-ci).
                var ion = Arg("-rwIonToken")
                    ?? Environment.GetEnvironmentVariable("ION_TOKEN")
                    ?? "";
                ion = ion.Trim();
                // Escape for a single-quoted JS string (token chars are usually alphanumeric + _.-).
                var ionJs = ion.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\r", "").Replace("\n", "");
                var ionLine = string.IsNullOrEmpty(ionJs)
                    ? ""
                    : "\n  window.RW_DEFAULT_ION_TOKEN = '" + ionJs + "';";

                // The page itself (title, icons, loader, COOP/COEP check) lives in Assets/WebGLTemplates/RocketWorld/index.html.
                string snippet = marker + (string.IsNullOrEmpty(ionJs) ? "" : "\n<script>" + ionLine.Trim() + "</script>\n");
                System.IO.File.WriteAllText(file, html.Replace("</body>", snippet + "</body>"));
                if (!string.IsNullOrEmpty(ion))
                    Debug.Log("[RocketWorld] baked Cesium ion token into index.html (from ION_TOKEN / -rwIonToken)");
                else
                    Debug.Log("[RocketWorld] no ION_TOKEN in environment — tiles will need ?ion=… or a key at runtime");
            }
            catch (Exception e) { Debug.LogWarning("[RocketWorld] could not post-process index.html: " + e.Message); }
        }

        public static void ConfigureWebGL()
        {
            PlayerSettings.productName = "Rocket World";
            PlayerSettings.companyName = "Rocket World";
            PlayerSettings.runInBackground = true;
            PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.stripEngineCode = true;
            PlayerSettings.SetManagedStrippingLevel(NamedBuildTarget.WebGL, ManagedStrippingLevel.Low);   // Cesium uses reflection-heavy interop
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.WebGL, ScriptingImplementation.IL2CPP);
            // WebGL 2 by default. `-rwGfx webgpu` (CI input "graphics") puts WebGPU first with WebGL 2 as the fallback, when this Unity version has it.
            var apis = new System.Collections.Generic.List<GraphicsDeviceType>();
            if (string.Equals(Arg("-rwGfx"), "webgpu", StringComparison.OrdinalIgnoreCase) && Enum.TryParse<GraphicsDeviceType>("WebGPU", out var gpu)) apis.Add(gpu);
            else if (string.Equals(Arg("-rwGfx"), "webgpu", StringComparison.OrdinalIgnoreCase)) Debug.LogWarning("[RocketWorld] this Unity version has no WebGPU graphics API; using WebGL 2");
            apis.Add(GraphicsDeviceType.OpenGLES3);
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL, apis.ToArray());
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.WebGL, false);

            PlayerSettings.WebGL.template = "PROJECT:RocketWorld";   // Assets/WebGLTemplates/RocketWorld
            // `-rwFast 1` (CI pull requests / throw-away builds): no Brotli pass (the slowest post-step), nothing is deployed from these.
            bool fast = Arg("-rwFast") == "1";
            PlayerSettings.WebGL.compressionFormat = fast ? WebGLCompressionFormat.Disabled : WebGLCompressionFormat.Brotli;
            if (fast) TryFastCodeOptimization();
            PlayerSettings.WebGL.decompressionFallback = false;     // vercel.json sends Content-Encoding: br
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.nameFilesAsHashes = true;          // content-hashed file names => safe to cache forever (see deploy/vercel.json)
            // OptimizeSize emits far less C++ for emscripten to compile + wasm-opt to chew through: much faster builds, smaller download.
            PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.WebGL, Arg("-rwSpeed") == "1" ? Il2CppCodeGeneration.OptimizeSpeed : Il2CppCodeGeneration.OptimizeSize);
            PlayerSettings.SetIl2CppCompilerConfiguration(NamedBuildTarget.WebGL, Il2CppCompilerConfiguration.Release);
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            // Mandatory for Cesium for Unity on the Web (see Documentation~/supported-platforms.md in the package).
            PlayerSettings.WebGL.threadsSupport = true;
            // Names differ between 6000.0.x patch releases; set the ones that exist.
            TrySet(typeof(PlayerSettings.WebGL), "wasm2023", true);
            // `-rwLite 1`: the low-memory build served at /lite/ (see Assets/WebGLTemplates/RocketWorld/index.html). iOS Safari kills the tab
            // ("A problem repeatedly occurred") when a shared wasm heap this big is reserved at startup; threads stay on (Cesium needs them).
            bool lite = Arg("-rwLite") == "1";
            if (lite) PlayerSettings.WebGL.dataCaching = false;   // no second in-memory copy of the .data file
            TrySet(typeof(PlayerSettings.WebGL), "initialMemorySize", lite ? 256 : 512);
            TrySet(typeof(PlayerSettings.WebGL), "maximumMemorySize", lite ? 1024 : 2048);
        }

        // Throw-away CI builds (pull requests): "Build Times" code optimization skips wasm-opt/LTO, the slowest part of the emscripten link.
        // Looked up by reflection because the type lives in the WebGL extension assembly and its name varies between Unity versions.
        static void TryFastCodeOptimization()
        {
            try
            {
                var t = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEditor.WebGL.UserBuildSettings", false)).FirstOrDefault(x => x != null);
                var p = t?.GetProperty("codeOptimization", BindingFlags.Public | BindingFlags.Static);
                if (p == null) { Debug.Log("[RocketWorld] WebGL codeOptimization not available, skipped"); return; }
                p.SetValue(null, Enum.Parse(p.PropertyType, "BuildTimes"));
                Debug.Log("[RocketWorld] fast build: WebGL code optimization = BuildTimes");
            }
            catch (Exception e) { Debug.LogWarning("[RocketWorld] could not set WebGL code optimization: " + e.Message); }
        }

        static void TrySet(Type t, string prop, object value)
        {
            var p = t.GetProperty(prop, BindingFlags.Public | BindingFlags.Static);
            if (p == null || !p.CanWrite) { Debug.Log($"[RocketWorld] PlayerSettings.WebGL.{prop} not available in this Unity version, skipped"); return; }
            try { p.SetValue(null, Convert.ChangeType(value, p.PropertyType)); } catch (Exception e) { Debug.LogWarning($"[RocketWorld] could not set {prop}: {e.Message}"); }
        }

        static string Arg(string name)
        {
            var a = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }
    }
}
