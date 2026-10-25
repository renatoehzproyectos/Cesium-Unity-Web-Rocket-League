using RocketWorld.Sim;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace RocketWorld
{
    /// <summary>
    /// Cinematic look: a global URP Volume (built in code, no assets) with bloom, Gaussian depth of field and camera motion blur.
    /// Each effect has its own slider in the settings (<see cref="GameSettings.fxBloom"/>, <see cref="GameSettings.fxDof"/>, <see cref="GameSettings.fxMotion"/>):
    /// 0 = off (not rendered at all), 1 = the designed look, 2 = twice as strong. HDR is used on desktop while bloom is on.
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
        DepthOfField _dof;
        MotionBlur _mb;
        int _applied = -1;
        Vector3 _lastCam;
        float _suppress, _mbCur, _statusAt;

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
        }

        void OnDestroy() { if (_profile != null) Destroy(_profile); }

        /// <summary>Which effects are on (bit 1 bloom, 2 depth of field, 4 motion blur), the camera post-processing flag, HDR.</summary>
        void Apply(int flags)
        {
            _applied = flags;
            bool bloom = (flags & 1) != 0, dof = (flags & 2) != 0, mb = (flags & 4) != 0;
            bool any = flags != 0;
            _vol.enabled = any;
            _bloom.active = bloom; _dof.active = dof; _mb.active = mb;
            bool hdr = bloom && !DeviceProfile.IsMobile;                      // HDR lets bloom pick real highlights; too heavy for phones
            _bloom.threshold.value = CinematicPolicy.BloomThreshold(hdr);
            _bloom.highQualityFiltering.value = hdr;
            var asset = UniversalRenderPipeline.asset;
            if (asset != null && asset.supportsHDR != hdr) asset.supportsHDR = hdr;
            if (root.cam != null)
            {
                root.cam.allowHDR = hdr;
                root.cam.GetUniversalAdditionalCameraData().renderPostProcessing = any;
            }
            _mbCur = 0f;
        }

        void LateUpdate()
        {
            if (_vol == null || root == null || root.cam == null || root.carCtl == null) return;
            var s = root.settings;
            float aBloom = CinematicPolicy.ClampAmount(s.fxBloom), aDof = CinematicPolicy.ClampAmount(s.fxDof), aMotion = CinematicPolicy.ClampAmount(s.fxMotion);
            int flags = (CinematicPolicy.IsOn(aBloom) ? 1 : 0) | (CinematicPolicy.IsOn(aDof) ? 2 : 0) | (CinematicPolicy.IsOn(aMotion) ? 4 : 0);
            if (flags != _applied) Apply(flags);
            if (flags == 0) { Status = "effects off"; return; }

            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.1f);
            var camPos = root.cam.transform.position;
            if ((camPos - _lastCam).sqrMagnitude > 80f * 80f) _suppress = 0.35f;   // teleport / respawn / camera swap: no smear for a moment
            _lastCam = camPos;
            _suppress = Mathf.Max(0f, _suppress - dt);

            var car = root.carCtl;
            float speed = car.SpeedMS;
            bool boosting = car.IsBoosting;

            if ((flags & 1) != 0)
                _bloom.intensity.value = CinematicPolicy.BloomIntensity(CinematicPolicy.Daylight(s.timeOfDay), boosting, aBloom);
            if ((flags & 2) != 0)
            {
                float start = CinematicPolicy.DofStart(Vector3.Distance(camPos, car.transform.position));
                _dof.gaussianStart.value = start;
                _dof.gaussianEnd.value = CinematicPolicy.DofEnd(start);
                _dof.gaussianMaxRadius.value = CinematicPolicy.DofMaxRadius(aDof);
            }
            if ((flags & 4) != 0)
            {
                float target = _suppress > 0f ? 0f : CinematicPolicy.MotionBlurIntensity(speed, boosting, aMotion);
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
            Status = "camera post-processing " + (data.renderPostProcessing ? "on" : "OFF") + " | volume in stack: " + resolved;
        }
    }
}
