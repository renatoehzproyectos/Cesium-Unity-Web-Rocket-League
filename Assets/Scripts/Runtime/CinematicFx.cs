using System.Reflection;
using RocketWorld.Sim;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RocketWorld
{
    /// <summary>
    /// Cinematic look: a global URP Volume (built in code, no assets) with bloom, colour grading (contrast, saturation, split toning),
    /// Gaussian depth of field and camera motion blur, plus the viewport edge blur, which is URP's own FullScreenPassRendererFeature running
    /// <c>RocketWorld/EdgeBlur</c> (registered in the renderer asset by ProjectSetup, switched on/off here).
    /// Each effect is a bit of <see cref="GameSettings.fxMask"/> (CinematicPolicy.Bloom / Grade / Edge / Dof / MotionBlur); Full also turns on HDR on desktop.
    /// All numbers come from <see cref="CinematicPolicy"/>. <see cref="Status"/> says what the stack is doing (shown in the settings panel).
    /// </summary>
    [DefaultExecutionOrder(200)]                 // after the cameras have moved this frame
    public class CinematicFx : MonoBehaviour
    {
        public GameRoot root;
        /// <summary>One-line state for the settings panel: lets you see from a phone whether the post-processing stack is really running.</summary>
        public string Status { get; private set; } = "";

        Volume _vol;
        VolumeProfile _profile;
        Bloom _bloom;
        ColorAdjustments _color;
        SplitToning _split;
        DepthOfField _dof;
        MotionBlur _mb;
        int _applied = -1;
        Vector3 _lastCam;
        float _suppress, _mbCur, _statusAt, _edgeStrength = 1f;
        ScriptableRendererFeature _edge;
        Material _edgeMat;
        bool _edgeSearched, _edgeWarned, _edgeOn;

        void Start()
        {
            bool mobile = DeviceProfile.IsMobile;
            var go = new GameObject("CinematicVolume");
            go.transform.SetParent(transform, false);
            _vol = go.AddComponent<Volume>();
            _vol.isGlobal = true; _vol.priority = 10f; _vol.weight = 1f;
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();

            _bloom = _profile.Add<Bloom>(false);
            _bloom.scatter.Override(0.65f);
            _bloom.tint.Override(new Color(1f, 0.97f, 0.92f));
            _bloom.downscale.Override(mobile ? BloomDownscaleMode.Quarter : BloomDownscaleMode.Half);
            _bloom.threshold.Override(CinematicPolicy.BloomThreshold(false));
            _bloom.intensity.Override(0.3f);
            _bloom.highQualityFiltering.Override(false);

            _color = _profile.Add<ColorAdjustments>(false);
            _color.postExposure.Override(0f);
            _color.contrast.Override(10f);
            _color.saturation.Override(12f);

            _split = _profile.Add<SplitToning>(false);                       // cool shadows, warm highlights
            _split.shadows.Override(new Color(0.78f, 0.88f, 1f));
            _split.highlights.Override(new Color(1f, 0.93f, 0.82f));
            _split.balance.Override(8f);

            _dof = _profile.Add<DepthOfField>(false);
            _dof.mode.Override(DepthOfFieldMode.Gaussian);
            _dof.gaussianStart.Override(120f);
            _dof.gaussianEnd.Override(820f);
            _dof.gaussianMaxRadius.Override(0.9f);
            _dof.highQualitySampling.Override(!mobile);

            _mb = _profile.Add<MotionBlur>(false);
            _mb.mode.Override(MotionBlurMode.CameraOnly);
            _mb.quality.Override(mobile ? MotionBlurQuality.Low : MotionBlurQuality.Medium);
            _mb.intensity.Override(0f);
            _mb.clamp.Override(0.06f);

            _vol.sharedProfile = _profile;
            Apply(root.settings.fxMask);
        }

        void OnDestroy()
        {
            _edgeOn = false; PushEdge();
            if (_profile != null) Destroy(_profile);
        }

        /// <summary>Apply an effect mask: which effects are on, the camera post-processing flag, HDR.</summary>
        void Apply(int mask)
        {
            _applied = mask;
            bool mobile = DeviceProfile.IsMobile;
            bool bloom = (mask & CinematicPolicy.Bloom) != 0, grade = (mask & CinematicPolicy.Grade) != 0, edge = (mask & CinematicPolicy.Edge) != 0;
            bool dof = (mask & CinematicPolicy.Dof) != 0, mb = (mask & CinematicPolicy.MotionBlur) != 0;
            bool anyVolume = bloom || grade || dof || mb;
            _vol.enabled = anyVolume;
            _bloom.active = bloom; _color.active = grade; _split.active = grade;
            _dof.active = dof; _mb.active = mb;
            bool hdr = (mask & CinematicPolicy.Full) == CinematicPolicy.Full && !mobile;   // HDR makes bloom pick real highlights; too heavy for phones
            _bloom.threshold.value = CinematicPolicy.BloomThreshold(hdr);
            _bloom.highQualityFiltering.value = hdr;
            var asset = UniversalRenderPipeline.asset;
            if (asset != null && asset.supportsHDR != hdr) asset.supportsHDR = hdr;
            if (root.cam != null)
            {
                root.cam.allowHDR = hdr;
                root.cam.GetUniversalAdditionalCameraData().renderPostProcessing = anyVolume || edge;
            }
            _edgeOn = edge;
            PushEdge();
            _mbCur = 0f;
        }

        void LateUpdate()
        {
            if (_vol == null || root == null || root.cam == null || root.carCtl == null) return;
            int mask = root.settings.fxMask;
            if (mask != _applied) Apply(mask);
            if (mask == 0) { Status = "effects off"; return; }

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            var camPos = root.cam.transform.position;
            if ((camPos - _lastCam).sqrMagnitude > 80f * 80f) _suppress = 0.35f;   // teleport / respawn / camera swap: no smear for a moment
            _lastCam = camPos;
            _suppress = Mathf.Max(0f, _suppress - dt);

            var car = root.carCtl;
            float speed = car.SpeedMS;
            bool boosting = car.IsBoosting;
            float day = CinematicPolicy.Daylight(root.settings.timeOfDay);

            if ((mask & CinematicPolicy.Bloom) != 0) _bloom.intensity.value = CinematicPolicy.BloomIntensity(day, boosting);
            if ((mask & CinematicPolicy.Grade) != 0)
            {
                _color.saturation.value = CinematicPolicy.Saturation(day);
                _color.contrast.value = CinematicPolicy.Contrast(day);
            }
            if (_edgeOn) { _edgeStrength = CinematicPolicy.EdgeBlurStrength(speed); PushEdge(); }

            if ((mask & (CinematicPolicy.Dof | CinematicPolicy.MotionBlur)) != 0)
            {
                float start = CinematicPolicy.DofStart(Vector3.Distance(camPos, car.transform.position));
                _dof.gaussianStart.value = start;
                _dof.gaussianEnd.value = CinematicPolicy.DofEnd(start);
                float target = _suppress > 0f ? 0f : CinematicPolicy.MotionBlurIntensity(speed, boosting);
                _mbCur = _suppress > 0f ? 0f : Mathf.MoveTowards(_mbCur, target, dt * 2f);
                _mb.intensity.value = _mbCur;
            }

            if (Time.unscaledTime >= _statusAt) { _statusAt = Time.unscaledTime + 0.5f; UpdateStatus(); }
        }

        void UpdateStatus()
        {
            var data = root.cam.GetUniversalAdditionalCameraData();
            var stack = VolumeManager.instance != null ? VolumeManager.instance.stack : null;
            var sb = stack != null ? stack.GetComponent<Bloom>() : null;
            string resolved = sb == null ? "n/a" : (sb.active ? "bloom " + sb.intensity.value.ToString("0.00") : "bloom off");
            Status = "fx " + root.settings.fxMask + " | camera post-processing " + (data.renderPostProcessing ? "on" : "OFF") +
                     " | volume in stack: " + resolved +
                     " | edge blur " + (_edge == null ? (_edgeOn ? "NOT FOUND" : "n/a") : (_edge.isActive ? "on" : "off"));
        }

        // ---- edge blur: URP FullScreenPassRendererFeature, found in the renderer asset by name ----

        void FindEdge()
        {
            _edgeSearched = true;
            _edgeMat = Resources.Load<Material>("Materials/EdgeBlur");
            try
            {
                var asset = UniversalRenderPipeline.asset;
                if (asset == null) return;
                var f = typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", BindingFlags.NonPublic | BindingFlags.Instance);
                var list = f != null ? f.GetValue(asset) as ScriptableRendererData[] : null;
                if (list == null) return;
                foreach (var d in list)
                {
                    if (d == null) continue;
                    foreach (var feat in d.rendererFeatures)
                        if (feat != null && feat.name == "RocketWorld Edge Blur") { _edge = feat; return; }
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[RocketWorld] could not look up the edge blur feature: " + e.Message); }
        }

        /// <summary>Switch the feature on/off and push the shader parameters. With the feature missing, strength 0 keeps the shader a no-op.</summary>
        void PushEdge()
        {
            if (!_edgeSearched) FindEdge();
            if (_edge != null && _edge.isActive != _edgeOn) _edge.SetActive(_edgeOn);
            if (_edgeMat != null)
            {
                _edgeMat.SetFloat("_Strength", _edgeOn ? _edgeStrength : 0f);
                _edgeMat.SetFloat("_Inner", 0.55f);
                _edgeMat.SetFloat("_Radius", 0.012f);
                _edgeMat.SetFloat("_Taps", DeviceProfile.IsMobile ? 8f : 12f);
            }
            if (_edgeOn && _edge == null && !_edgeWarned)
            {
                _edgeWarned = true;
                Debug.LogWarning("[RocketWorld] edge blur renderer feature not found: run RocketWorld > Ensure project setup and rebuild.");
            }
        }
    }
}
