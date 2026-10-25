using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace RocketWorld
{
    /// <summary>
    /// URP renderer feature that blurs the borders of the viewport (left/right walls, ceiling, floor) with <c>RocketWorld/EdgeBlur</c>.
    /// It runs after the post-processing stack and before the final SMAA, only on the main camera, and only while <see cref="Active"/> is true
    /// (so it costs nothing when cinematic effects are off). <see cref="CinematicFx"/> drives the static parameters every frame.
    /// ProjectSetup adds this feature to the renderer asset.
    /// </summary>
    public class EdgeBlurFeature : ScriptableRendererFeature
    {
        public static bool Active;
        public static float Strength = 1f, Inner = 0.55f, Radius = 0.012f, Taps = 10f;

        Material _mat;
        Pass _pass;

        public override void Create()
        {
            _mat = Resources.Load<Material>("Materials/EdgeBlur");
            _pass = new Pass { renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!Active) return;
            if (_mat == null) _mat = Resources.Load<Material>("Materials/EdgeBlur");     // created by ProjectSetup after the feature on a fresh checkout
            if (_mat == null || _pass == null) return;
            var cam = renderingData.cameraData.camera;
            if (cam.cameraType != CameraType.Game || cam.targetTexture != null || !cam.CompareTag("MainCamera")) return;   // not the streaming / minimap cameras
            _pass.material = _mat;
            renderer.EnqueuePass(_pass);
        }

        class Pass : ScriptableRenderPass
        {
            public Material material;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                if (resources.isActiveTargetBackBuffer) return;                 // nothing to read from: the stack renders straight to the screen
                var cameraData = frameData.Get<UniversalCameraData>();
                int w = cameraData.cameraTargetDescriptor.width, h = cameraData.cameraTargetDescriptor.height;

                TextureHandle source = resources.activeColorTexture;
                var desc = renderGraph.GetTextureDesc(source);
                desc.name = "RW_EdgeBlur";
                desc.clearBuffer = false;
                TextureHandle destination = renderGraph.CreateTexture(desc);

                material.SetFloat("_Strength", Strength);
                material.SetFloat("_Inner", Inner);
                material.SetFloat("_Radius", Radius);
                material.SetFloat("_Taps", Taps);
                material.SetVector("_Dims", new Vector4(w, h, 1f / Mathf.Max(1, w), 1f / Mathf.Max(1, h)));

                var parameters = new RenderGraphUtils.BlitMaterialParameters(source, destination, material, 0);
                renderGraph.AddBlitPass(parameters, passName: "RW Edge Blur");
                resources.cameraColor = destination;                            // the final SMAA / blit reads the blurred image
            }
        }
    }
}
