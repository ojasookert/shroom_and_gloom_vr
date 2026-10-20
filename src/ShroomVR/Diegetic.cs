using System;
using UnityEngine;

namespace ShroomVR;

/// <summary>
/// Things the game shows as 2D props in front of the camera that belong in your hands in VR.
/// The map: the game raises a hand holding the map ("Map Reminder Hand") into view; in VR that whole
/// prop follows the left controller, turned towards your eyes so it stays readable. Because it really
/// is there, the laser can point at the map.
/// </summary>
public static class Diegetic
{
    static Transform _mapReminder;   // the prop's root, which the game leaves static under DelayedViewUI
    static Transform _mapHandRoot;   // animated by S_MapReminder when the map opens/closes

    /// <summary>Is the map currently raised?</summary>
    public static bool MapOpen => FindMap() && _mapHandRoot.gameObject.activeInHierarchy;

    static bool FindMap()
    {
        if (_mapReminder != null && _mapHandRoot != null) return true;
        var reminder = UnityEngine.Object.FindObjectOfType<S_MapReminder>();
        if (reminder == null) return false;
        _mapReminder = reminder.transform;
        _mapHandRoot = _mapReminder.Find("HandRoot");
        return _mapHandRoot != null;
    }

    /// <summary>Called from onBeforeRender, after the hands have their poses for this frame.</summary>
    public static void Update(HandRig left)
    {
        if (!VRConfig.MapInHand.Value || left == null || !left.Tracked) return;
        try
        {
            if (!FindMap()) return;
            var grip = left.Grip;
            float scale = Mathf.Max(0.2f, VRConfig.MapScale.Value) * VRRig.Scale;

            // Held just above and in front of the controller, facing the eyes.
            var pos = grip.position + grip.rotation * new Vector3(0f, 0.07f, 0.06f) * VRRig.Scale;
            var toEyes = pos - VRRig.HeadPosition;
            if (toEyes.sqrMagnitude < 1e-6f) return;
            var rot = Quaternion.LookRotation(toEyes.normalized, grip.rotation * Vector3.up);

            // Place the root so that the open-pose HandRoot ends up at pos/rot (the game animates
            // HandRoot locally for open/close, which then plays relative to the controller).
            var openLocal = Matrix4x4.TRS(OpenLocalPosition, Quaternion.identity, Vector3.one);
            var rootWorld = Matrix4x4.TRS(pos, rot, Vector3.one * scale) * openLocal.inverse;
            _mapReminder.SetPositionAndRotation(rootWorld.MultiplyPoint3x4(Vector3.zero), rot);
            var parentScale = _mapReminder.parent != null ? _mapReminder.parent.lossyScale : Vector3.one;
            _mapReminder.localScale = new Vector3(scale / NonZero(parentScale.x), scale / NonZero(parentScale.y), scale / NonZero(parentScale.z));
        }
        catch (Exception e)
        {
            VRRig.LogOnce("map in hand", e);
        }
    }

    /// <summary>HandRoot's local position when the map is fully raised (measured in game).</summary>
    static readonly Vector3 OpenLocalPosition = new(-0.13f, -0.07f, 0.15f);

    static float NonZero(float v) => Mathf.Abs(v) < 1e-6f ? 1e-6f : v;
}
