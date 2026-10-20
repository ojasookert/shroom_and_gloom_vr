using System;
using HarmonyLib;
using UnityEngine;

namespace ShroomVR;

/// <summary>
/// Comfort: camera shake and head bob are motion the player didn't make, so in VR they become
/// controller rumble instead.
/// </summary>
[HarmonyPatch]
public static class ComfortPatches
{
    static bool NoShake => VRSystem.Enabled && VRConfig.DisableCameraShake.Value;

    static void Rumble(float duration, float strength)
    {
        if (!VRSystem.Enabled || !VRConfig.HapticsFromShake.Value) return;
        float amp = Mathf.Clamp01(0.25f + strength * 0.5f) * VRConfig.HapticStrength.Value;
        float secs = Mathf.Clamp(duration, 0.02f, 0.6f);
        VRInput.Haptic(Hand.Left, secs, 60f, amp);
        VRInput.Haptic(Hand.Right, secs, 60f, amp);
    }

    [HarmonyPatch(typeof(S_CameraShake), nameof(S_CameraShake.ShakeRumble), typeof(float), typeof(float), typeof(float), typeof(Vector3))]
    [HarmonyPrefix]
    static bool ShakeRumbleAt(float duration, float strengthM) { Rumble(duration, strengthM); return !NoShake; }

    [HarmonyPatch(typeof(S_CameraShake), nameof(S_CameraShake.ShakeRumble), typeof(float), typeof(float), typeof(float), typeof(float))]
    [HarmonyPrefix]
    static bool ShakeRumbleDist(float duration, float strengthM) { Rumble(duration, strengthM); return !NoShake; }

    [HarmonyPatch(typeof(S_CameraShake), nameof(S_CameraShake.ShakeRumble), typeof(float), typeof(float), typeof(float), typeof(bool), typeof(float))]
    [HarmonyPrefix]
    static bool ShakeRumbleFade(float duration, float strengthM) { Rumble(duration, strengthM); return !NoShake; }

    [HarmonyPatch(typeof(S_CameraShake), nameof(S_CameraShake.ShakeY), typeof(float), typeof(float), typeof(float), typeof(bool))]
    [HarmonyPrefix]
    static bool ShakeY(float duration, float strengthM) { Rumble(duration, strengthM); return !NoShake; }

    [HarmonyPatch(typeof(S_CameraShake), nameof(S_CameraShake.ShakeY), typeof(Vector3), typeof(float), typeof(float))]
    [HarmonyPrefix]
    static bool ShakeYAt(float duration, float strengthM) { Rumble(duration, strengthM); return !NoShake; }

    [HarmonyPatch(typeof(S_CameraShake), nameof(S_CameraShake.LateUpdate))]
    [HarmonyPrefix]
    static bool ShakeLateUpdate(S_CameraShake __instance)
    {
        if (!NoShake) return true;
        __instance.transform.localPosition = Vector3.zero;
        __instance.transform.localRotation = Quaternion.identity;
        return false;
    }

    [HarmonyPatch(typeof(Movement), nameof(Movement.BobHead))]
    [HarmonyPrefix]
    static bool BobHead() => !(VRSystem.Enabled && VRConfig.DisableViewBob.Value);
}

/// <summary>Keeps modded crashes out of the developers' crash reports.</summary>
public static class CrashReporting
{
    [System.Runtime.InteropServices.DllImport("sentry", EntryPoint = "sentry_close")]
    static extern int NativeClose();

    /// <summary>Delete native crash reports queued by earlier modded sessions before they get uploaded.</summary>
    public static void PurgeQueuedReports()
    {
        try
        {
            var dir = System.IO.Path.Combine(Application.persistentDataPath, "SentryNative");
            foreach (var sub in new[] { "reports", "attachments" })
            {
                var d = System.IO.Path.Combine(dir, sub);
                if (!System.IO.Directory.Exists(d)) continue;
                foreach (var f in System.IO.Directory.GetFiles(d)) System.IO.File.Delete(f);
                foreach (var f in System.IO.Directory.GetDirectories(d)) System.IO.Directory.Delete(f, true);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("Could not purge queued crash reports: " + e.Message);
        }
    }

    public static void Disable()
    {
        try
        {
            Sentry.SentrySdk.Close();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("Could not close managed Sentry: " + e.Message);
        }
        try
        {
            NativeClose(); // stops the crashpad handler from capturing (and uploading) native crashes
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("Could not close native Sentry: " + e.Message);
        }
        PurgeQueuedReports();
        Plugin.Logger.LogInfo("Crash reporting disabled for this modded session.");
    }
}
