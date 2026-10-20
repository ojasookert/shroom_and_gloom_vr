using System;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace ShroomVR;

/// <summary>
/// Owns the mapping between SteamVR tracking space and the game world, and steers the game's own
/// cameras at render time.
///
/// The game's logic (pointer rays, card layout, UI raycasts) keeps seeing ordinary flat cameras: they
/// are only switched to stereo, moved to the head pose and given per-eye view matrices between
/// Application.onBeforeRender and the end of the frame. The "anchor" is the game camera's flat pose
/// (yaw only), so walking, encounters and cutscene moves all carry the player along.
/// </summary>
public class VRRig : MonoBehaviour
{
    public VRRig(IntPtr ptr) : base(ptr) { }

    public static VRRig Instance { get; private set; }

    /// <summary>Tracking space → world, as of this frame's onBeforeRender.</summary>
    public static Matrix4x4 TrackingToWorld { get; private set; } = Matrix4x4.identity;
    public static Quaternion TrackingToWorldRotation { get; private set; } = Quaternion.identity;
    public static float Scale => Mathf.Max(0.05f, VRConfig.WorldScale.Value);

    public static Vector3 HeadPosition { get; private set; }
    public static Quaternion HeadRotation { get; private set; } = Quaternion.identity;

    /// <summary>The flat game camera all logic and UI mapping is relative to (usually Camera.main).</summary>
    public static Camera ReferenceCamera { get; private set; }
    public static Matrix4x4 ReferenceFlat { get; private set; } = Matrix4x4.identity;

    public Transform Root { get; private set; }

    /// <summary>
    /// Camera the player currently interacts through. Normally the reference camera; while the deck view
    /// is open it is the deck camera, which renders the deck elsewhere in the world.
    /// </summary>
    public static Camera InteractionCamera { get; private set; }

    /// <summary>Maps poses relative to the reference camera into the interaction camera's space.</summary>
    public static Matrix4x4 ViewRelative { get; private set; } = Matrix4x4.identity;

    public static bool InOverlayView => InteractionCamera != null && InteractionCamera != ReferenceCamera;

    static CardDisplayParent _deck;
    static Camera _deckCam;

    /// <summary>
    /// The game renders the deck view from a camera placed 3.75 m above the player. In VR that would
    /// yank your viewpoint; instead the deck display is kept exactly at the main camera's pose, so the
    /// deck opens right in front of you in the world you are standing in.
    /// </summary>
    internal static void KeepDeckInWorld()
    {
        var main = ReferenceCamera;
        if (main == null) return;
        OverlayViewCamera();
        if (_deck == null || _deckCam == null) return;
        var root = _deck.transform;
        SeeThroughDeckBackground();
        // Deck Display Camera sits at the display root's origin; align the camera with the main camera.
        var camLocal = Matrix4x4.TRS(_deckCam.transform.localPosition, _deckCam.transform.localRotation, Vector3.one);
        var rootWorld = main.transform.localToWorldMatrix * camLocal.inverse;
        root.SetPositionAndRotation(rootWorld.MultiplyPoint3x4(Vector3.zero), rootWorld.rotation);
    }

    static Transform _deckBehind;
    static UnityEngine.UI.Image[] _deckImages = Array.Empty<UnityEngine.UI.Image>();
    static float _nextDeckImageLookup;
    static readonly Dictionary<IntPtr, float> _deckAlpha = new();

    /// <summary>
    /// The deck view covers the world with opaque images (a black backdrop and its wipe-in transition).
    /// Like the pause menu, they are dimmed instead, so you still see where you are. Alpha the game
    /// animates is followed, not compounded.
    /// </summary>
    static void SeeThroughDeckBackground()
    {
        if (_deckBehind == null)
        {
            _deckBehind = _deck.transform.Find("Deck Displays Canvas/Deck Display/UIBehindCardsRect");
            if (_deckBehind == null) return;
        }
        if (!_deckBehind.gameObject.activeInHierarchy) return;
        if (Time.unscaledTime >= _nextDeckImageLookup)
        {
            _nextDeckImageLookup = Time.unscaledTime + 1f;
            _deckImages = _deckBehind.GetComponentsInChildren<UnityEngine.UI.Image>(true);
        }
        float opacity = Mathf.Clamp01(VRConfig.DeckBackgroundOpacity.Value);
        foreach (var image in _deckImages)
        {
            if (image == null) continue;
            var c = image.color;
            if (!_deckAlpha.TryGetValue(image.Pointer, out var gameAlpha) || Mathf.Abs(c.a - gameAlpha * opacity) > 0.001f)
                _deckAlpha[image.Pointer] = gameAlpha = c.a; // first sight, or the game changed it
            float want = gameAlpha * opacity;
            if (Mathf.Abs(c.a - want) > 0.001f) image.color = new Color(c.r, c.g, c.b, want);
        }
    }

    static Camera OverlayViewCamera()
    {
        if (_deckCam == null)
        {
            if (_deck == null) _deck = UnityEngine.Object.FindObjectOfType<CardDisplayParent>();
            if (_deck != null) _deckCam = _deck.GetComponentInChildren<Camera>(true);
        }
        return _deckCam != null && _deckCam.isActiveAndEnabled ? _deckCam : null;
    }
    public HandRig Left { get; private set; }
    public HandRig Right { get; private set; }

    Vector3 _recenterPos;
    Quaternion _recenterRot = Quaternion.identity;
    bool _recenterPending = true;
    bool _snapLatch;

    readonly TrackedDevicePose_t[] _renderPoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    readonly TrackedDevicePose_t[] _gamePoses = new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    Matrix4x4 _headTracking = Matrix4x4.identity;
    Matrix4x4 _eyeLeft = Matrix4x4.identity, _eyeRight = Matrix4x4.identity;
    float _nextEyeRefresh;

    class CamState
    {
        public Camera Cam;
        public Matrix4x4 Flat;
        public bool Overridden;
        public Vector3 SavedLocalPos;
        public Quaternion SavedLocalRot;
        public StereoTargetEyeMask SavedEye;
        public bool SavedOrtho;
        public float FlatFov = 60f;
        public int Depth;
        public float RestoredFov = -1f;
        public int Frame;
        /// <summary>The camera carries content that belongs to the world, not to your head (the deck's cards).</summary>
        public bool PinContent;
        public readonly List<(Transform t, Vector3 localPos, Quaternion localRot, Vector3 pos, Quaternion rot)> Pinned = new();
    }

    readonly Dictionary<IntPtr, CamState> _cams = new();
    UnityEngine.Events.UnityAction _beforeRender;
    readonly List<CamState> _active = new();
    bool _inRender;

    public static void Create()
    {
        var go = new GameObject("ShroomVR Rig");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.DontSave;
        Instance = go.AddComponent<VRRig>();
    }

    void Awake()
    {
        Root = transform;
        CreateClearCamera();
        Left = HandRig.Create(Root, Hand.Left);
        Right = HandRig.Create(Root, Hand.Right);

        _beforeRender = (UnityEngine.Events.UnityAction)OnBeforeRender; // keep the delegate alive
        Application.add_onBeforeRender(_beforeRender);
        Plugin.Logger.LogInfo("VR rig ready");
    }

    static Camera.CameraCallback Combine(Camera.CameraCallback a, Camera.CameraCallback b) =>
        a == null ? b : Il2CppSystem.Delegate.Combine(a, b).Cast<Camera.CameraCallback>();

    public void Recenter() => _recenterPending = true;

    /// <summary>Console override: pin the head to the recenter pose (repeatable screenshots with an idle headset).</summary>
    public static bool DebugFixedHead;
    public static float DebugHeadYaw;

    /// <summary>
    /// Some scenes (title, loading) only have depth-clearing UI cameras: on a monitor the UI covers
    /// every pixel, in a headset the space around the virtual screen would show stale frames.
    /// </summary>
    void CreateClearCamera()
    {
        var go = new GameObject("ShroomVR Clear Camera");
        go.transform.SetParent(transform, false);
        var cam = go.AddComponent<Camera>();
        cam.cullingMask = 0;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.depth = -1000;
        cam.useOcclusionCulling = false;
        cam.allowHDR = false;
        cam.allowMSAA = false;
    }

    // ---------------------------------------------------------------- per frame (logic) --------

    void Update()
    {
        VRFrame.Tick();
        HandleTurning();
        if (VRInput.GetDownAny(VRAction.Recenter)) Recenter();
    }

    void HandleTurning()
    {
        var stick = VRInput.GetStick(Hand.Right);
        switch (VRConfig.Turn.Value)
        {
            case TurnMode.Snap:
                if (Mathf.Abs(stick.x) > 0.75f && !_snapLatch)
                {
                    _snapLatch = true;
                    CameraLook.TurnYaw += Mathf.Sign(stick.x) * VRConfig.SnapTurnAngle.Value;
                }
                else if (Mathf.Abs(stick.x) < 0.35f) _snapLatch = false;
                break;
            case TurnMode.Smooth:
                if (Mathf.Abs(stick.x) > 0.2f) CameraLook.TurnYaw += stick.x * VRConfig.SmoothTurnSpeed.Value * Time.unscaledDeltaTime;
                break;
        }
    }

    void LateUpdate()
    {
        CameraLook.ApplyVRLook();
        KeepDeckInWorld();
    }

    void Start() => this.StartCoroutine(EndOfFrameLoop().WrapToIl2Cpp());

    System.Collections.IEnumerator EndOfFrameLoop()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;
            EndFrame();
        }
    }

    // ---------------------------------------------------------------- render time --------------

    void OnBeforeRender()
    {
        if (!VRSystem.Enabled) return;
        try
        {
            Crumbs.Mark("obr look");
            if (!Debug.Skip("look")) CameraLook.ApplyVRLook();
            Crumbs.Mark("obr refcam");
            ReferenceCamera = PickReferenceCamera();
            if (ReferenceCamera == null)
            {
                // Nothing to anchor to (mid-load): still keep cameras flat for logic and stereo for render.
                if (!Debug.Skip("cams")) PrepareCameras();
                return;
            }
            ReferenceFlat = ReferenceCamera.transform.localToWorldMatrix;
            KeepDeckInWorld();

            Crumbs.Mark("obr head");
            ReadHeadPose();
            UpdateRigMatrix();
            Crumbs.Mark("obr hands");
            if (!Debug.Skip("hands")) { Left.UpdatePose(); Right.UpdatePose(); }
            Diegetic.Update(Left);
            if (!Debug.Skip("cardhand")) CardHand.Apply(VRConfig.LeftHanded.Value ? Right : Left, Laser.Instance?.ActiveHand == Hand.Left ? Left : Right);
            Crumbs.Mark("obr ui");
            if (!Debug.Skip("ui")) VRUI.Place();
            Crumbs.Mark("obr laser");
            if (!Debug.Skip("laser")) Laser.Instance?.UpdateVisual();
            Crumbs.Mark("obr cams");
            if (!Debug.Skip("cams")) PrepareCameras();
            Crumbs.Mark("obr done");
        }
        catch (Exception e)
        {
            LogOnce("onBeforeRender", e);
        }
    }

    static Camera PickReferenceCamera()
    {
        var main = Camera.main;
        if (main != null && main.isActiveAndEnabled && main.targetTexture == null) return main;
        return ReferenceCamera != null && ReferenceCamera.isActiveAndEnabled ? ReferenceCamera : main;
    }

    void ReadHeadPose()
    {
        var compositor = OpenVR.Compositor;
        if (compositor == null) return;
        compositor.GetLastPoses(_renderPoses, _gamePoses);
        var hmd = _renderPoses[OpenVR.k_unTrackedDeviceIndex_Hmd];
        if (DebugFixedHead) _headTracking = Matrix4x4.TRS(_recenterPos, _recenterRot * Quaternion.Euler(0, DebugHeadYaw, 0), Vector3.one);
        else if (!hmd.bPoseIsValid) return;
        else _headTracking = VRMath.ToUnityMatrix(hmd.mDeviceToAbsoluteTracking);

        if (Time.unscaledTime >= _nextEyeRefresh) // IPD can change at runtime
        {
            _nextEyeRefresh = Time.unscaledTime + 1f;
            _eyeLeft = VRMath.ToUnityMatrix(OpenVR.System.GetEyeToHeadTransform(EVREye.Eye_Left));
            _eyeRight = VRMath.ToUnityMatrix(OpenVR.System.GetEyeToHeadTransform(EVREye.Eye_Right));
        }

        if (_recenterPending)
        {
            _recenterPending = false;
            _recenterPos = _headTracking.GetColumn(3);
            var fwd = _headTracking.rotation * Vector3.forward;
            fwd.y = 0;
            _recenterRot = fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd.normalized) : Quaternion.identity;
            CameraLook.TurnYaw = 0;
            Plugin.Logger.LogInfo($"Recentered at {_recenterPos}");
        }
    }

    void UpdateRigMatrix()
    {
        var anchorPos = ReferenceFlat.MultiplyPoint3x4(Vector3.zero) + Vector3.up * VRConfig.HeightOffset.Value;
        var anchorRot = CameraLook.AnchorYaw(ReferenceFlat);

        var overlay = OverlayViewCamera();
        InteractionCamera = overlay != null ? overlay : ReferenceCamera;
        ViewRelative = overlay != null ? overlay.transform.localToWorldMatrix * ReferenceFlat.inverse : Matrix4x4.identity;

        TrackingToWorldRotation = anchorRot * Quaternion.Inverse(_recenterRot);
        TrackingToWorld = Matrix4x4.TRS(anchorPos, anchorRot, Vector3.one * Scale)
                          * Matrix4x4.Inverse(Matrix4x4.TRS(_recenterPos, _recenterRot, Vector3.one));
        // The hands live in the interaction camera's space (identity unless an overlay view is open).
        var rootWorld = ViewRelative * TrackingToWorld;
        Root.SetPositionAndRotation(rootWorld.MultiplyPoint3x4(Vector3.zero), ViewRelative.rotation * TrackingToWorldRotation);
        Left.SetLayer(VRLayers.Hands);
        Right.SetLayer(VRLayers.Hands);
        Root.localScale = Vector3.one * Scale;

        var head = TrackingToWorld * _headTracking;
        HeadPosition = head.MultiplyPoint3x4(Vector3.zero);
        HeadRotation = TrackingToWorldRotation * _headTracking.rotation;
    }

    /// <summary>Head pose in world as a rigid transform (scale removed).</summary>
    static Matrix4x4 Rigid(Vector3 pos, Quaternion rot) => Matrix4x4.TRS(pos, rot, Vector3.one);

    void PrepareCameras()
    {
        _active.Clear();
        int frame = Time.frameCount;
        foreach (var cam in Cameras.Enabled()) // enabled cameras only
        {
            Crumbs.Mark("prep " + (cam != null ? cam.name : "null"));
            if (cam == null || cam.targetTexture != null || cam.cameraType != CameraType.Game) continue;
            if (!_cams.TryGetValue(cam.Pointer, out var st) || st.Cam == null)
            {
                st = new CamState { Cam = cam };
                _cams[cam.Pointer] = st;
                XRDevice.DisableAutoXRCameraTracking(cam, true);
                // XR has usually already pushed the headset FOV into the camera; the game's own cameras use 60.
                st.FlatFov = cam.fieldOfView > 90f ? 60f : cam.fieldOfView;
            }
            st.Flat = cam.transform.localToWorldMatrix;
            st.Frame = frame;
            st.SavedEye = cam.stereoTargetEye;
            st.SavedOrtho = cam.orthographic;
            st.SavedLocalPos = cam.transform.localPosition;
            st.SavedLocalRot = cam.transform.localRotation;
            st.Depth = Depth(cam.transform);
            st.PinContent = cam == _deckCam;
            // XR overwrites fieldOfView with the headset's; only accept changes the game itself made.
            float f = cam.fieldOfView;
            if (Mathf.Abs(f - st.RestoredFov) > 0.01f && f < 90f) st.FlatFov = f;
            if (cam.orthographic) cam.orthographic = false; // loading screens: ortho has no meaning in stereo
            cam.stereoTargetEye = StereoTargetEyeMask.Both;
            _active.Add(st);
        }

        // Everything is applied here, before either eye renders: in multi-pass Unity has already taken
        // the left eye's matrices by the time Camera.onPreCull runs. Parents go first, because moving a
        // parent camera drags its child cameras along.
        _active.Sort((a, b) => a.Depth.CompareTo(b.Depth));
        foreach (var st in _active) ApplyOverride(st);
        _inRender = true;
    }

    static int Depth(Transform t)
    {
        int d = 0;
        while ((t = t.parent) != null) d++;
        return d;
    }

    void ApplyOverride(CamState st)
    {
        var cam = st.Cam;
        try
        {
            // Camera pose relative to the reference camera is preserved, so overlay/deck/inspect cameras
            // stay registered with the main view while following the head.
            var relative = st.Flat * ReferenceFlat.inverse;
            var camWorld = relative * Rigid(HeadPosition, HeadRotation);
            // The deck view's cards hang under its camera; they stay where the game put them, next to
            // the deck panel, instead of riding along with your head.
            st.Pinned.Clear();
            if (st.PinContent)
            {
                var ct = cam.transform;
                for (int i = 0; i < ct.childCount; i++)
                {
                    var c = ct.GetChild(i);
                    if (c.GetComponent<Camera>() == null) st.Pinned.Add((c, c.localPosition, c.localRotation, c.position, c.rotation));
                }
            }
            cam.transform.SetPositionAndRotation(camWorld.MultiplyPoint3x4(Vector3.zero), camWorld.rotation);
            foreach (var p in st.Pinned) p.t.SetPositionAndRotation(p.pos, p.rot);
            st.Overridden = true;
            cam.SetStereoViewMatrix(Camera.StereoscopicEye.Left, EyeView(relative, _eyeLeft));
            cam.SetStereoViewMatrix(Camera.StereoscopicEye.Right, EyeView(relative, _eyeRight));
        }
        catch (Exception e)
        {
            LogOnce("camera override " + cam.name, e);
        }
    }

    Matrix4x4 EyeView(Matrix4x4 relative, Matrix4x4 eyeToHead)
    {
        var eyeTracking = _headTracking * eyeToHead;
        var pos = TrackingToWorld.MultiplyPoint3x4(eyeTracking.GetColumn(3));
        var rot = TrackingToWorldRotation * eyeTracking.rotation;
        var eyeWorld = relative * Rigid(pos, rot);
        // Unity view matrices look down -Z.
        return Matrix4x4.Scale(new Vector3(1, 1, -1)) * Rigid(eyeWorld.MultiplyPoint3x4(Vector3.zero), eyeWorld.rotation).inverse;
    }

    /// <summary>Called from the end-of-frame hook: give the game its flat cameras back.</summary>
    internal void EndFrame()
    {
        Crumbs.Mark("endframe");
        CardHand.Restore();
        if (!_inRender) return;
        _inRender = false;
        foreach (var st in _active)
        {
            var cam = st.Cam;
            if (cam == null) continue;
            if (st.Overridden)
            {
                cam.transform.localPosition = st.SavedLocalPos;
                cam.transform.localRotation = st.SavedLocalRot;
                st.Overridden = false;
            }
            foreach (var p in st.Pinned)
            {
                if (p.t == null) continue;
                p.t.localPosition = p.localPos;
                p.t.localRotation = p.localRot;
            }
            st.Pinned.Clear();
            cam.ResetStereoViewMatrices();
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.orthographic = st.SavedOrtho;
            cam.fieldOfView = st.FlatFov;
            st.RestoredFov = st.FlatFov;
            // XR also forces the eye texture's (square) aspect, which would squeeze every screen<->world
            // conversion the game makes horizontally.
            cam.ResetAspect();
        }
        _active.Clear();
    }

    /// <summary>Re-assert flat FOVs before game logic runs (XR keeps pushing the headset FOV).</summary>
    internal void RestoreFlatFov()
    {
        foreach (var st in _cams.Values)
        {
            if (st.Cam == null || st.RestoredFov < 0) continue;
            if (st.Cam.fieldOfView > 90f) st.Cam.fieldOfView = st.FlatFov;
            if (Mathf.Abs(st.Cam.aspect - (float)Screen.width / Screen.height) > 0.001f) st.Cam.ResetAspect();
        }
    }

    static readonly HashSet<string> Logged = new();

    internal static void LogOnce(string where, Exception e)
    {
        if (Logged.Add(where + e.GetType().Name)) Plugin.Logger.LogError($"[{where}] {e}");
    }
}
