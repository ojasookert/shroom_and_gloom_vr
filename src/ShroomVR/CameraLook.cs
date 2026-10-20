using System;
using HarmonyLib;
using UnityEngine;

namespace ShroomVR;

/// <summary>
/// In VR the head does the looking. The game's mouse/stick look (which treats the absolute pointer as
/// a joystick) is switched off and the "View" transform instead becomes the frame the UI hangs on:
/// either fixed in front of the body or lazily following the head.
/// </summary>
[HarmonyPatch]
public static class CameraLook
{
    static PlayerCamView _view;
    static Quaternion _followRot = Quaternion.identity;
    static bool _following;

    public static float TurnYaw;

    static float _walkingRootY = float.NaN;
    /// <summary>Diagnostic: how far the player root is above its last tunnel (walking) height.</summary>
    public static float RoomLift { get; private set; }

    /// <summary>
    /// When an encounter starts (explore room, combat, camp, shop, reward choices) the game glides the
    /// player to the encounter's viewing spot, which sits ~0.6 m above walking height to frame the room
    /// on a monitor: a camera move you didn't make. In VR the glide keeps your current height (it still
    /// moves you horizontally and turns you), so the game, its cameras and everything hanging off them
    /// simply stay at walking height. Drops (target below you) are left alone.
    /// </summary>
    [HarmonyPatch(typeof(Movement), nameof(Movement.MoveToInteraction), typeof(Transform), typeof(float), typeof(bool))]
    [HarmonyPostfix]
    static void KeepHeightToDummy(Movement __instance) => KeepHeight(__instance);

    [HarmonyPatch(typeof(Movement), nameof(Movement.MoveToInteraction), typeof(Vector3), typeof(Quaternion), typeof(float), typeof(bool))]
    [HarmonyPostfix]
    static void KeepHeightToPoint(Movement __instance) => KeepHeight(__instance);

    static void KeepHeight(Movement m)
    {
        if (!VRSystem.Enabled || !VRConfig.NoRoomLift.Value) return;
        try
        {
            var start = m._autoMoveStartPosition;
            var target = m._autoMoveTarget;
            if (target.y <= start.y) return;
            Plugin.Logger.LogInfo($"Encounter glide kept at walking height (game wanted +{target.y - start.y:F2} m)");
            target.y = start.y;
            m._autoMoveTarget = target;
        }
        catch (Exception e)
        {
            VRRig.LogOnce("keep height", e);
        }
    }

    static void TrackLift(PlayerCamView v)
    {
        // Keeps the game's _lookFrame current (normally done by Look(), which VR skips). It throws until
        // the player is fully set up.
        try { v.UpdateLookLimits(); } catch { return; }
        float rootY = v.transform.parent.position.y;
        if (v._lookFrame == PlayerCamView.LookFrame.Tunnel || float.IsNaN(_walkingRootY)) _walkingRootY = rootY;
        RoomLift = rootY - _walkingRootY;
    }

    /// <summary>Extra downward tilt of the UI frame in body mode; puts the hand of cards at a natural reading angle.</summary>
    public const float BodyTilt = 8f;

    static PlayerCamView View
    {
        get
        {
            if (_view == null) _view = UnityEngine.Object.FindObjectOfType<PlayerCamView>();
            return _view;
        }
    }

    /// <summary>The player's body orientation (yaw only) plus stick turning; the VR tracking space is anchored to it.</summary>
    public static Quaternion AnchorYaw(Matrix4x4 referenceFlat)
    {
        Vector3 fwd;
        var v = View;
        if (v != null && v.isActiveAndEnabled && v.transform.parent != null && VRRig.ReferenceCamera != null &&
            VRRig.ReferenceCamera.transform.IsChildOf(v.transform))
            fwd = v.transform.parent.forward;
        else
            fwd = referenceFlat.MultiplyVector(Vector3.forward);
        fwd.y = 0;
        var yaw = fwd.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(fwd.normalized) : Quaternion.identity;
        return yaw * Quaternion.Euler(0, TurnYaw, 0);
    }

    public static void ApplyVRLook()
    {
        if (!VRSystem.Enabled) return;
        var v = View;
        if (v == null || v.transform.parent == null) return;
        try
        {
            TrackLift(v);
            var t = v.transform;
            var turn = Quaternion.Euler(0, TurnYaw, 0);
            if (VRConfig.UiFollow.Value == UiAnchor.Head)
            {
                // Head rotation relative to the body+turn frame, without roll.
                var body = Quaternion.LookRotation(Flat(t.parent.forward)) * turn;
                var rel = Quaternion.Inverse(body) * VRRig.HeadRotation;
                var e = rel.eulerAngles;
                var target = Quaternion.Euler(e.x, e.y, 0);
                float angle = Quaternion.Angle(_followRot, target);
                if (angle > VRConfig.UiFollowThreshold.Value) _following = true;
                if (_following)
                {
                    _followRot = Quaternion.Slerp(_followRot, target, 1f - Mathf.Exp(-VRConfig.UiFollowSpeed.Value * Time.unscaledDeltaTime));
                    if (angle < 2f) _following = false;
                }
                t.localRotation = turn * _followRot;
            }
            else
            {
                t.localRotation = turn * Quaternion.Euler(BodyTilt, 0, 0);
            }
        }
        catch (Exception e)
        {
            VRRig.LogOnce("ApplyVRLook", e);
        }
    }

    static Vector3 Flat(Vector3 v)
    {
        v.y = 0;
        return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
    }

    [HarmonyPatch(typeof(PlayerCamView), nameof(PlayerCamView.Look))]
    [HarmonyPrefix]
    static bool LookPrefix() => !VRSystem.Enabled;

    [HarmonyPatch(typeof(PlayerCamView), nameof(PlayerCamView.TryGetNormalisedLookInput))]
    [HarmonyPrefix]
    static bool NoLookInput(ref bool __result, ref Vector2 lookInput)
    {
        if (!VRSystem.Enabled) return true;
        lookInput = Vector2.zero;
        __result = false;
        return false;
    }
}
