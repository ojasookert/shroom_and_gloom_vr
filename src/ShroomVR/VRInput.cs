using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using Valve.VR;

namespace ShroomVR;

public enum Hand { Left = 0, Right = 1 }

public enum VRAction
{
    // Boolean actions
    Click,
    Confirm,
    Cancel,
    Pause,
    Inspect,
    DeckView,
    Map,
    SortHand,
    Recenter,
    PagePrev,
    PageNext,
    Run,
    Grab,
    EndTurn,
    ToggleLaserHand,
    NavUp,
    NavDown,
    NavLeft,
    NavRight,
    // Vector2 actions
    Stick,
}

/// <summary>
/// SteamVR Input (IVRInput) wrapper for the /actions/main set declared in shroomvr_actions.json.
/// The XR plugin registers the manifest; this class only resolves handles and polls state.
/// Digital states are cached once per frame in <see cref="Update"/> so Get/GetDown/GetUp agree
/// for every caller within a frame.
/// </summary>
public static class VRInput
{
    public static string ManifestFileName = "shroomvr_actions.json";

    const string SetPath = "/actions/main";
    const int BoolCount = (int)VRAction.NavRight + 1;
    const int HandCount = 2;

    static readonly string[] BoolPaths =
    {
        "/actions/main/in/Click",
        "/actions/main/in/Confirm",
        "/actions/main/in/Cancel",
        "/actions/main/in/Pause",
        "/actions/main/in/Inspect",
        "/actions/main/in/DeckView",
        "/actions/main/in/Map",
        "/actions/main/in/SortHand",
        "/actions/main/in/Recenter",
        "/actions/main/in/PagePrev",
        "/actions/main/in/PageNext",
        "/actions/main/in/Run",
        "/actions/main/in/Grab",
        "/actions/main/in/EndTurn",
        "/actions/main/in/ToggleLaserHand",
        "/actions/main/in/NavUp",
        "/actions/main/in/NavDown",
        "/actions/main/in/NavLeft",
        "/actions/main/in/NavRight",
    };

    static ulong _set;
    static readonly ulong[] _bool = new ulong[BoolCount];
    static ulong _squeeze, _gripSqueeze, _stick, _poseAim, _poseGrip, _haptic;
    static readonly ulong[] _hand = new ulong[HandCount];

    static readonly bool[,] _cur = new bool[BoolCount, HandCount];
    static readonly bool[,] _prev = new bool[BoolCount, HandCount];
    static readonly float[] _squeezeValue = new float[HandCount];
    static readonly bool[] _squeezeHeld = new bool[HandCount];
    static readonly Vector2[] _stickValue = new Vector2[HandCount];
    static readonly bool[] _handActive = new bool[HandCount];
    static readonly uint[] _deviceIndex = { OpenVR.k_unTrackedDeviceIndexInvalid, OpenVR.k_unTrackedDeviceIndexInvalid };

    static readonly VRActiveActionSet_t[] _activeSets = new VRActiveActionSet_t[1];
    static InputDigitalActionData_t _digital;
    static InputAnalogActionData_t _analog;
    static InputPoseActionData_t _pose;
    static InputOriginInfo_t _origin;

    static readonly uint ActiveSetSize = (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t));
    static readonly uint DigitalSize = (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t));
    static readonly uint AnalogSize = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));
    static readonly uint PoseSize = (uint)Marshal.SizeOf(typeof(InputPoseActionData_t));
    static readonly uint OriginSize = (uint)Marshal.SizeOf(typeof(InputOriginInfo_t));

    static float _nextErrorLog;

    public static bool Ready { get; private set; }

    public static bool Init()
    {
        Ready = false;
        try
        {
            var input = OpenVR.Input;
            if (input == null)
            {
                Plugin.Logger.LogError("VRInput: OpenVR.Input unavailable (is OpenVR initialized?)");
                return false;
            }

            bool ok = Check(input.GetActionSetHandle(SetPath, ref _set), SetPath);
            for (int i = 0; i < BoolCount; i++)
                ok &= Check(input.GetActionHandle(BoolPaths[i], ref _bool[i]), BoolPaths[i]);
            ok &= Check(input.GetActionHandle("/actions/main/in/Squeeze", ref _squeeze), "Squeeze");
            ok &= Check(input.GetActionHandle("/actions/main/in/GripSqueeze", ref _gripSqueeze), "GripSqueeze");
            ok &= Check(input.GetActionHandle("/actions/main/in/Stick", ref _stick), "Stick");
            ok &= Check(input.GetActionHandle("/actions/main/in/PoseAim", ref _poseAim), "PoseAim");
            ok &= Check(input.GetActionHandle("/actions/main/in/PoseGrip", ref _poseGrip), "PoseGrip");
            ok &= Check(input.GetActionHandle("/actions/main/out/Haptic", ref _haptic), "Haptic");
            ok &= Check(input.GetInputSourceHandle("/user/hand/left", ref _hand[(int)Hand.Left]), "/user/hand/left");
            ok &= Check(input.GetInputSourceHandle("/user/hand/right", ref _hand[(int)Hand.Right]), "/user/hand/right");

            _activeSets[0] = new VRActiveActionSet_t
            {
                ulActionSet = _set,
                ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle,
                ulSecondaryActionSet = OpenVR.k_ulInvalidActionSetHandle,
                nPriority = 0,
            };

            Ready = ok;
            Plugin.Logger.LogInfo($"VRInput initialised (ok={ok})");
            return ok;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("VRInput.Init: " + e);
            return false;
        }
    }

    static bool Check(EVRInputError err, string what)
    {
        if (err == EVRInputError.None) return true;
        Plugin.Logger.LogError($"VRInput: {what} -> {err}");
        return false;
    }

    /// <summary>Poll all actions. Call exactly once per frame, before any Get* queries.</summary>
    public static void Update()
    {
        if (!Ready) return;
        try
        {
            var input = OpenVR.Input;
            if (input == null) return;

            Array.Copy(_cur, _prev, _cur.Length);

            var err = input.UpdateActionState(_activeSets, ActiveSetSize);
            if (err != EVRInputError.None)
            {
                LogThrottled("UpdateActionState: " + err);
                Array.Clear(_cur, 0, _cur.Length);
                Array.Clear(_stickValue, 0, HandCount);
                ApplyDebug();
                return;
            }

            for (int h = 0; h < HandCount; h++)
            {
                ulong restrict = _hand[h];
                for (int a = 0; a < BoolCount; a++)
                {
                    _cur[a, h] = input.GetDigitalActionData(_bool[a], ref _digital, DigitalSize, restrict) == EVRInputError.None
                                 && _digital.bActive && _digital.bState;
                }

                // Click is the trigger (or grip) pulled past the threshold, not only the hardware click at
                // the end of its travel.
                float trigger = input.GetAnalogActionData(_squeeze, ref _analog, AnalogSize, restrict) == EVRInputError.None
                                && _analog.bActive ? _analog.x : 0f;
                float grip = input.GetAnalogActionData(_gripSqueeze, ref _analog, AnalogSize, restrict) == EVRInputError.None
                             && _analog.bActive ? _analog.x : 0f;
                if (_debugSqueezeUntil[h] > Time.unscaledTime) trigger = _debugSqueeze[h];
                _squeezeValue[h] = Mathf.Max(trigger, grip);
                float press = Mathf.Clamp(VRConfig.TriggerThreshold.Value, 0.05f, 1f);
                _squeezeHeld[h] = _squeezeValue[h] >= press || (_squeezeHeld[h] && _squeezeValue[h] > press - ClickHysteresis);
                _cur[(int)VRAction.Click, h] |= _squeezeHeld[h];
                _stickValue[h] = input.GetAnalogActionData(_stick, ref _analog, AnalogSize, restrict) == EVRInputError.None
                                 && _analog.bActive ? new Vector2(_analog.x, _analog.y) : Vector2.zero;

                // Hand liveness + device index come from the aim pose's active origin.
                bool active = input.GetPoseActionDataForNextFrame(_poseAim, ETrackingUniverseOrigin.TrackingUniverseStanding,
                                  ref _pose, PoseSize, restrict) == EVRInputError.None && _pose.bActive;
                _handActive[h] = active && _pose.pose.bDeviceIsConnected;
                _deviceIndex[h] = OpenVR.k_unTrackedDeviceIndexInvalid;
                if (active && _pose.activeOrigin != OpenVR.k_ulInvalidInputValueHandle &&
                    input.GetOriginTrackedDeviceInfo(_pose.activeOrigin, ref _origin, OriginSize) == EVRInputError.None)
                    _deviceIndex[h] = _origin.trackedDeviceIndex;
            }

            ApplyDebug();
        }
        catch (Exception e)
        {
            LogThrottled("VRInput.Update: " + e);
        }
    }

    static void LogThrottled(string msg)
    {
        if (Time.unscaledTime < _nextErrorLog) return;
        _nextErrorLog = Time.unscaledTime + 5f;
        Plugin.Logger.LogWarning(msg);
    }

    static bool IsBool(VRAction a) => (int)a < BoolCount;

    public static bool Get(VRAction a, Hand h) => IsBool(a) && _cur[(int)a, (int)h];
    public static bool GetDown(VRAction a, Hand h) => IsBool(a) && _cur[(int)a, (int)h] && !_prev[(int)a, (int)h];
    public static bool GetUp(VRAction a, Hand h) => IsBool(a) && !_cur[(int)a, (int)h] && _prev[(int)a, (int)h];
    public static bool GetAny(VRAction a) => Get(a, Hand.Left) || Get(a, Hand.Right);
    public static bool GetDownAny(VRAction a) => GetDown(a, Hand.Left) || GetDown(a, Hand.Right);
    public static bool GetUpAny(VRAction a) => GetUp(a, Hand.Left) || GetUp(a, Hand.Right);

    const float ClickHysteresis = 0.15f;

    /// <summary>Analog value: trigger (or grip) squeeze (0..1) for Click, 1/0 for other boolean actions.</summary>
    public static float GetValue(VRAction a, Hand h)
    {
        if (a == VRAction.Click) return _squeezeValue[(int)h];
        return Get(a, h) ? 1f : 0f;
    }

    /// <summary>A hand's thumbstick (or trackpad), -1..1 on each axis.</summary>
    public static Vector2 GetStick(Hand h) => _stickValue[(int)h];

    // ---- console-driven controller input for testing without controllers in hand ----
    static readonly Vector2[] _debugStick = new Vector2[HandCount];
    static readonly float[] _debugStickUntil = new float[HandCount];
    static readonly int[,] _debugPress = new int[BoolCount, HandCount];
    static readonly float[] _debugSqueeze = new float[HandCount];
    static readonly float[] _debugSqueezeUntil = new float[HandCount];

    /// <summary>Hold a hand's trigger pulled to a value (0..1) for the given time.</summary>
    public static void DebugSqueeze(Hand h, float value, float seconds)
    {
        _debugSqueeze[(int)h] = value;
        _debugSqueezeUntil[(int)h] = Time.unscaledTime + seconds;
    }

    /// <summary>Hold a hand's stick at a position for the given time.</summary>
    public static void DebugStick(Hand h, Vector2 v, float seconds)
    {
        _debugStick[(int)h] = v;
        _debugStickUntil[(int)h] = Time.unscaledTime + seconds;
    }

    /// <summary>Hold a boolean action on one hand for the given number of frames.</summary>
    public static void DebugPress(VRAction a, Hand h, int frames)
    {
        if (IsBool(a)) _debugPress[(int)a, (int)h] = Mathf.Max(1, frames);
    }

    static void ApplyDebug()
    {
        for (int h = 0; h < HandCount; h++)
        {
            if (_debugStickUntil[h] > Time.unscaledTime) _stickValue[h] = _debugStick[h];
            for (int a = 0; a < BoolCount; a++)
                if (_debugPress[a, h] > 0) { _cur[a, h] = true; _debugPress[a, h]--; }
        }
    }

    /// <summary>
    /// Live pose of a hand in the compositor's tracking space (Unity convention, meters).
    /// aim = pointing pose (/pose/tip), otherwise the grip pose (/pose/raw).
    /// </summary>
    public static bool TryGetPose(Hand h, bool aim, out Vector3 pos, out Quaternion rot, out Vector3 vel, out Vector3 angVel)
    {
        pos = Vector3.zero; rot = Quaternion.identity; vel = Vector3.zero; angVel = Vector3.zero;
        if (!Ready) return false;
        try
        {
            var input = OpenVR.Input;
            var compositor = OpenVR.Compositor;
            if (input == null || compositor == null) return false;
            var space = compositor.GetTrackingSpace();
            if (input.GetPoseActionDataForNextFrame(aim ? _poseAim : _poseGrip, space, ref _pose, PoseSize, _hand[(int)h]) != EVRInputError.None
                || !_pose.bActive || !_pose.pose.bPoseIsValid)
                return false;
            VRMath.ToUnity(_pose.pose.mDeviceToAbsoluteTracking, out pos, out rot);
            vel = VRMath.ToUnityVector(_pose.pose.vVelocity);
            angVel = VRMath.ToUnityAngularVelocity(_pose.pose.vAngularVelocity);
            return true;
        }
        catch (Exception e)
        {
            LogThrottled("VRInput.TryGetPose: " + e);
            return false;
        }
    }

    public static void Haptic(Hand h, float seconds, float frequency, float amplitude)
    {
        if (!Ready) return;
        try
        {
            OpenVR.Input?.TriggerHapticVibrationAction(_haptic, 0f, seconds, frequency, Mathf.Clamp01(amplitude), _hand[(int)h]);
        }
        catch (Exception e)
        {
            LogThrottled("VRInput.Haptic: " + e);
        }
    }

    /// <summary>Tracked device index of the controller in this hand (k_unTrackedDeviceIndexInvalid if none).</summary>
    public static uint DeviceIndex(Hand h) => _deviceIndex[(int)h];

    public static bool IsActive(Hand h) => _handActive[(int)h];

    /// <summary>
    /// Short label of what the action is bound to, e.g. "Right A Button". Allocates; call on demand
    /// (prompt refresh), not every frame.
    /// </summary>
    public static string BindingHint(VRAction a)
    {
        if (!Ready) return "";
        try
        {
            var input = OpenVR.Input;
            if (input == null) return "";
            ulong action = a == VRAction.Stick ? _stick : _bool[(int)a];
            var origins = new ulong[OpenVR.k_unMaxActionOriginCount];
            if (input.GetActionOrigins(_set, action, origins) != EVRInputError.None) return "";
            var names = new List<string>();
            var sb = new StringBuilder(128);
            foreach (var o in origins)
            {
                if (o == OpenVR.k_ulInvalidInputValueHandle) continue;
                sb.Clear();
                if (input.GetOriginLocalizedName(o, sb, 128,
                        (int)(EVRInputStringBits.VRInputString_Hand | EVRInputStringBits.VRInputString_InputSource)) != EVRInputError.None)
                    continue;
                var s = sb.ToString();
                if (s.Length > 0 && !names.Contains(s)) names.Add(s);
            }
            return string.Join(" / ", names);
        }
        catch (Exception e)
        {
            LogThrottled("VRInput.BindingHint: " + e);
            return "";
        }
    }

    /// <summary>SteamVR input source handle for /user/hand/left or /user/hand/right.</summary>
    public static ulong InputSource(Hand h) => _hand[(int)h];
}
