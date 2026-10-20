using System;
using UnityEngine;

namespace ShroomVR;

/// <summary>
/// One tracked controller under the rig root (which is tracking space). "Grip" follows the raw device
/// pose and carries the render model; "Aim" follows the pointing pose and is where the laser starts.
/// </summary>
public class HandRig
{
    public Hand Side { get; private set; }
    public Transform Grip { get; private set; }
    public Transform Aim { get; private set; }
    public bool Tracked { get; private set; }
    public Vector3 Velocity { get; private set; }

    ControllerModel _model;

    public static HandRig Create(Transform root, Hand side)
    {
        var h = new HandRig { Side = side };
        h.Grip = new GameObject(side + " Grip").transform;
        h.Grip.SetParent(root, false);
        h.Aim = new GameObject(side + " Aim").transform;
        h.Aim.SetParent(root, false);
        return h;
    }

    int _layer = -1;

    /// <summary>Overlay views render on other layers; the controller model follows.</summary>
    public void SetLayer(int layer)
    {
        if (layer == _layer) return;
        _layer = layer;
        foreach (var t in Grip.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;
    }

    public void EnsureModel()
    {
        if (_model != null || !VRConfig.ShowControllers.Value) return;
        try
        {
            _model = ControllerModel.Create(Grip, VRLayers.Hands, () => VRInput.DeviceIndex(Side), () => VRInput.InputSource(Side));
        }
        catch (Exception e)
        {
            VRRig.LogOnce("controller model", e);
        }
    }

    /// <summary>
    /// Console override for testing without a controller in hand: a fake pose in tracking space (under
    /// the rig root), so it moves with the player exactly like a real controller.
    /// </summary>
    public (Vector3 pos, Quaternion rot)? DebugAim;

    /// <summary>Set the fake pose from a world pose as of now.</summary>
    public void SetDebugAimWorld(Vector3 pos, Quaternion rot)
    {
        var root = Grip.parent;
        DebugAim = (root.InverseTransformPoint(pos), Quaternion.Inverse(root.rotation) * rot);
    }

    public void UpdatePose()
    {
        EnsureModel();
        Tracked = false;
        if (DebugAim.HasValue)
        {
            Grip.localPosition = Aim.localPosition = DebugAim.Value.pos;
            Grip.localRotation = Aim.localRotation = DebugAim.Value.rot;
            Tracked = true;
            Grip.gameObject.SetActive(true);
            return;
        }
        if (VRInput.TryGetPose(Side, false, out var gp, out var gr, out var vel, out _))
        {
            Grip.localPosition = gp;
            Grip.localRotation = gr;
            Velocity = VRRig.TrackingToWorldRotation * vel * VRRig.Scale;
            Tracked = true;
        }
        if (VRInput.TryGetPose(Side, true, out var ap, out var ar, out _, out _))
        {
            Aim.localPosition = ap;
            Aim.localRotation = ar;
        }
        else if (Tracked)
        {
            Aim.localPosition = Grip.localPosition;
            Aim.localRotation = Grip.localRotation;
        }
        Grip.gameObject.SetActive(Tracked);
    }
}

/// <summary>Layers used for our own objects. Values come from the game's layer table.</summary>
public static class VRLayers
{
    public const int Default = 0;
    public const int UI = 5;
    public const int Card = 7;
    public const int WorldUI = 9;
    public const int DeckDisplay = 13;

    /// <summary>
    /// Controllers render in the UI pass with the cards and panels (in the world pass every panel would
    /// be drawn over your hands). The build has no depth-writing textured shader, so they are sorted
    /// after the panels instead: your hands are always in front of the HUD, as they are in reality.
    /// </summary>
    public static int Hands = WorldUI;
    public const short HandsOrder = 32000;
    /// <summary>The laser renders in the UI pass so it is never hidden behind cards or panels.</summary>
    public static int Laser = WorldUI;
}

/// <summary>
/// Camera.allCameras crashes through the interop layer (the unstripped getter writes into a managed
/// array the engine does not expect), so cameras are fetched into a reused native array instead.
/// </summary>
public static class Cameras
{
    static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Camera> _buf = new(16);
    static readonly System.Collections.Generic.List<Camera> _list = new();

    public static System.Collections.Generic.List<Camera> Enabled()
    {
        _list.Clear();
        int count = Camera.allCamerasCount;
        if (count > _buf.Length) _buf = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Camera>(count * 2);
        int n = Camera.GetAllCameras(_buf);
        for (int i = 0; i < n && i < _buf.Length; i++)
        {
            var c = _buf[i];
            if (c != null) _list.Add(c);
        }
        return _list;
    }
}
