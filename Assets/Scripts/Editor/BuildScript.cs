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

        // Adds a clear message when the host forgot the COOP/COEP headers (threaded WASM cannot start without them).
        static void PostProcessIndex(string dir)
        {
            try
            {
                var file = System.IO.Path.Combine(dir, "index.html");
                var html = System.IO.File.ReadAllText(file);
                const string marker = "<!-- rocketworld-checks -->";
                if (html.Contains(marker) || !html.Contains("</body>")) return;
                const string snippet = marker + @"
<script>
  (function () {
    var msg = null;
    if (!window.crossOriginIsolated) msg = 'This page is not cross-origin isolated (COOP/COEP headers missing), so the 3D engine cannot start. Check vercel.json / your host headers.';
    else if (!window.WebGL2RenderingContext) msg = 'This browser does not support WebGL 2.';
    if (!msg) return;
    var d = document.createElement('div');
    d.style.cssText = 'position:fixed;left:0;right:0;top:0;padding:14px 18px;background:#b00020;color:#fff;font:14px sans-serif;z-index:99999';
    d.textContent = msg; document.body.appendChild(d);
  })();
</script>
<style>
  #rw-loading{position:fixed;inset:0;background:#0b1620;display:flex;flex-direction:column;align-items:center;justify-content:center;z-index:9999;color:#cfe6f2;font:16px sans-serif;transition:opacity .5s}
  #rw-loading .sp{width:44px;height:44px;border:4px solid #25415a;border-top-color:#cfe6f2;border-radius:50%;animation:rwspin 1s linear infinite;margin-bottom:16px}
  @keyframes rwspin{to{transform:rotate(360deg)}}
</style>
<div id=""rw-loading""><div class=""sp""></div><div>Loading RocketWorld…</div><div style=""opacity:.6;font-size:13px;margin-top:6px"">First load downloads the engine; later visits are cached.</div></div>
";
                System.IO.File.WriteAllText(file, html.Replace("</body>", snippet + "</body>"));
            }
            catch (Exception e) { Debug.LogWarning("[RocketWorld] could not post-process index.html: " + e.Message); }
        }

        public static void ConfigureWebGL()
        {
            PlayerSettings.productName = "RocketWorld";
            PlayerSettings.companyName = "RocketWorld";
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

            PlayerSettings.WebGL.template = "APPLICATION:Default";
            // `-rwFast 1` (CI pull requests / throw-away builds): no Brotli pass (the slowest post-step), nothing is deployed from these.
            bool fast = Arg("-rwFast") == "1";
            PlayerSettings.WebGL.compressionFormat = fast ? WebGLCompressionFormat.Disabled : WebGLCompressionFormat.Brotli;
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
            TrySet(typeof(PlayerSettings.WebGL), "initialMemorySize", 512);
            TrySet(typeof(PlayerSettings.WebGL), "maximumMemorySize", 2048);
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
