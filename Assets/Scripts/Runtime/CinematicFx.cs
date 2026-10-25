using RocketWorld.Sim;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RocketWorld
{
    /// <summary>
    /// Cinematic look: a global URP Volume (built in code, no assets) with bloom, colour grading (contrast, saturation, split toning),
    /// Gaussian depth of field and camera motion blur, plus the viewport edge blur (<see cref="EdgeBlurFeature"/>).
    /// Each effect is a bit of <see cref="GameSettings.fxMask"/> (CinematicPolicy.Bloom / Grade / Edge / Dof / MotionBlur); Full also turns on HDR on desktop.
    /// All numbers come from <see cref="CinematicPolicy"/>. Not verified in Unity: the look needs tuning on a real build.
    /// </summary>
    [DefaultExecutionOrder(200)]                 // after the cameras have moved this frame
    public class CinematicFx : MonoBehaviour
    {
        public GameRoot root;

        Volume _vol;
        VolumeProfile _profile;
        Bloom _bloom;
        ColorAdjustments _color;
        SplitToning _split;
        DepthOfField _dof;
        MotionBlur _mb;
        int _applied = -1;
        Vector3 _lastCam;
        float _suppress, _mbCur;

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
            EdgeBlurFeature.Active = false;
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
            bool hdr = (mask & CinematicPolicy.Full) == CinematicPolicy.Full && !mobile;                                       // HDR makes bloom pick real highlights; too heavy for phones
            _bloom.threshold.value = CinematicPolicy.BloomThreshold(hdr);
            _bloom.highQualityFiltering.value = hdr;
            var asset = UniversalRenderPipeline.asset;
            if (asset != null && asset.supportsHDR != hdr) asset.supportsHDR = hdr;
            if (root.cam != null)
            {
                root.cam.allowHDR = hdr;
                root.cam.GetUniversalAdditionalCameraData().renderPostProcessing = anyVolume || edge;
            }
            EdgeBlurFeature.Active = edge;
            EdgeBlurFeature.Taps = mobile ? 8f : 12f;
            EdgeBlurFeature.Inner = 0.55f;
            EdgeBlurFeature.Radius = 0.012f;
            _mbCur = 0f;
        }

        void LateUpdate()
        {
            if (_vol == null || root == null || root.cam == null || root.carCtl == null) return;
            int mask = root.settings.fxMask;
            if (mask != _applied) Apply(mask);
            if (mask == 0) return;

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
            EdgeBlurFeature.Strength = CinematicPolicy.EdgeBlurStrength(speed);

            if ((mask & (CinematicPolicy.Dof | CinematicPolicy.MotionBlur)) != 0)
            {
                float start = CinematicPolicy.DofStart(Vector3.Distance(camPos, car.transform.position));
                _dof.gaussianStart.value = start;
                _dof.gaussianEnd.value = CinematicPolicy.DofEnd(start);
                float target = _suppress > 0f ? 0f : CinematicPolicy.MotionBlurIntensity(speed, boosting);
                _mbCur = _suppress > 0f ? 0f : Mathf.MoveTowards(_mbCur, target, dt * 2f);
                _mb.intensity.value = _mbCur;
            }
        }
    }
}
