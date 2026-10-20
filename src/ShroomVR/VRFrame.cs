using System;
using UnityEngine;

namespace ShroomVR;

/// <summary>
/// Runs the per-frame VR input work exactly once, on whichever comes first: our Update or the game's
/// first input query of the frame. That keeps "pressed this frame" edges visible to the game for
/// exactly one frame no matter how script execution is ordered.
/// </summary>
public static class VRFrame
{
    static int _frame = -1;
    static bool _busy;

    public static void Tick()
    {
        if (!VRSystem.Enabled || _busy || _frame == Time.frameCount) return;
        _frame = Time.frameCount;
        _busy = true;
        try
        {
            VRRig.Instance?.RestoreFlatFov();
            Crumbs.Mark("tick input");
            VRInput.Update();
            StickNav.Update();
            Crumbs.Mark("tick laser");
            if (!Debug.Skip("laser")) Laser.Instance?.UpdateTargeting();
            Crumbs.Mark("tick bridge");
            InputBridge.Compute();
            Crumbs.Mark("tick mouse");
            VirtualMouse.Flush();
            Crumbs.Mark("tick done");
        }
        catch (Exception e)
        {
            VRRig.LogOnce("VRFrame", e);
        }
        finally
        {
            _busy = false;
        }
    }
}

/// <summary>Diagnostics: SHROOMVR_SKIP=ui,laser,cams,... disables rig stages for bisecting problems.</summary>
public static class Debug
{
    static readonly string[] Skips = (System.Environment.GetEnvironmentVariable("SHROOMVR_SKIP") ?? "").Split(',');
    public static bool Skip(string stage) => System.Array.IndexOf(Skips, stage) >= 0;
}

/// <summary>Diagnostics: SHROOMVR_CRUMBS=1 writes the last executed stage to disk (survives hard crashes).</summary>
public static class Crumbs
{
    static readonly bool On = System.Environment.GetEnvironmentVariable("SHROOMVR_CRUMBS") == "1";
    static System.IO.FileStream _fs;
    static readonly byte[] _buf = new byte[256];

    public static void Mark(string stage)
    {
        if (!On) return;
        try
        {
            _fs ??= new System.IO.FileStream(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "shroomvr_crumbs.txt"),
                System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.ReadWrite, 1, System.IO.FileOptions.WriteThrough);
            var line = $"{Time.frameCount} {Time.realtimeSinceStartup:F2} {stage}\n";
            int n = System.Text.Encoding.ASCII.GetBytes(line, 0, System.Math.Min(line.Length, 255), _buf, 0);
            _fs.Write(_buf, 0, n);
        }
        catch { }
    }
}
