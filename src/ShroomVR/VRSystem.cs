using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace ShroomVR;

/// <summary>
/// Starts Unity's XR display subsystem with Valve's OpenVR XR plugin (XRSDKOpenVR.dll, registered
/// through Shroom and Gloom_Data/UnitySubsystems). Unity then renders every stereo camera to the
/// headset itself; we only steer the cameras (see <see cref="VRRig"/>).
/// </summary>
public static class VRSystem
{
    public static bool Enabled { get; private set; }
    public static XRDisplaySubsystem Display { get; private set; }

    const string DisplayId = "OpenVR Display";
    const string InputId = "OpenVR Input";
    public static XRInputSubsystem Input { get; private set; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct UserDefinedSettings
    {
        public ushort stereoRenderingMode;
        public ushort initializationType;
        public ushort mirrorViewMode;
        [MarshalAs(UnmanagedType.LPStr)] public string editorAppKey;
        [MarshalAs(UnmanagedType.LPStr)] public string actionManifestPath;
        [MarshalAs(UnmanagedType.LPStr)] public string applicationName;
    }

    [DllImport("XRSDKOpenVR", CharSet = CharSet.Ansi)]
    static extern void SetUserDefinedSettings(UserDefinedSettings settings);

    [DllImport("XRSDKOpenVR")]
    static extern EVRInitError GetInitializationResult();

    [DllImport("XRSDKOpenVR")]
    static extern void SetMirrorViewMode(ushort mirrorViewMode);

    [DllImport("openvr_api", EntryPoint = "VR_IsHmdPresent")]
    static extern bool IsHmdPresent();

    [DllImport("openvr_api", EntryPoint = "VR_IsRuntimeInstalled")]
    static extern bool IsRuntimeInstalled();

    [DllImport("openvr_api", EntryPoint = "VR_InitInternal2")]
    static extern uint InitInternal2(ref EVRInitError error, EVRApplicationType type, [MarshalAs(UnmanagedType.LPStr)] string startupInfo);

    [DllImport("openvr_api", EntryPoint = "VR_ShutdownInternal")]
    static extern void ShutdownInternal();

    /// <summary>
    /// True when SteamVR is already running. A background-mode init never launches SteamVR, so this is
    /// a safe probe; VR_IsHmdPresent alone reports false while a wireless headset idles in standby.
    /// </summary>
    static bool SteamVRRunning()
    {
        var err = EVRInitError.None;
        InitInternal2(ref err, EVRApplicationType.VRApplication_Background, null);
        ShutdownInternal();
        Plugin.Logger.LogInfo($"SteamVR probe: {err}");
        // Only "no server" means SteamVR is down; anything else (including interface or headset errors
        // from a sleeping wireless headset) means the server answered.
        return err != EVRInitError.Init_NoServerForBackgroundApp;
    }

    static string PluginDir => Path.Combine(Application.dataPath, "Plugins", "x86_64");
    public static string GameDir => Path.GetDirectoryName(Application.dataPath);

    static bool _resolverInstalled;

    /// <summary>Make our P/Invokes bind to the same DLLs Unity's XR plugin loads.</summary>
    internal static void InstallResolver()
    {
        if (_resolverInstalled) return;
        _resolverInstalled = true;
        NativeLibrary.SetDllImportResolver(typeof(VRSystem).Assembly, (name, asm, path) =>
        {
            if (name is "openvr_api" or "XRSDKOpenVR" or "sentry")
            {
                var full = Path.Combine(PluginDir, name + ".dll");
                if (File.Exists(full) && NativeLibrary.TryLoad(full, out var h)) return h;
            }
            return IntPtr.Zero;
        });
    }

    public static bool ShouldStart()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => a.Equals("-shroomvr-novr", StringComparison.OrdinalIgnoreCase))) return false;
        bool forced = args.Any(a => a.Equals("-shroomvr-vr", StringComparison.OrdinalIgnoreCase));
        if (!forced)
        {
            if (VRConfig.Mode.Value == VRModeSetting.Off) return false;
            forced = VRConfig.Mode.Value == VRModeSetting.On;
        }

        InstallResolver();
        if (!File.Exists(Path.Combine(PluginDir, "XRSDKOpenVR.dll")))
        {
            Plugin.Logger.LogError($"XRSDKOpenVR.dll missing from {PluginDir}; the mod is not installed correctly. Staying flat.");
            return false;
        }
        try
        {
            if (!IsRuntimeInstalled())
            {
                Plugin.Logger.LogWarning("SteamVR runtime not installed; staying flat.");
                return false;
            }
            if (!forced && !IsHmdPresent() && !SteamVRRunning())
            {
                Plugin.Logger.LogInfo("SteamVR is not running; staying flat (start SteamVR first, or use -shroomvr-vr to force).");
                return false;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("openvr_api probe failed: " + e.Message);
            return false;
        }
        return true;
    }

    public static bool Start()
    {
        if (Enabled) return true;
        try
        {
            var manifest = Path.Combine(GameDir, VRInput.ManifestFileName);
            SetUserDefinedSettings(new UserDefinedSettings
            {
                stereoRenderingMode = 0, // multi-pass: safest with the game's custom shaders and post-processing
                initializationType = 1,  // scene application
                mirrorViewMode = (ushort)Mathf.Clamp(VRConfig.MirrorEye.Value, 0, 3),
                editorAppKey = null,
                actionManifestPath = manifest,
                applicationName = "Shroom and Gloom VR",
            });

            // SubsystemManager.GetSubsystemDescriptors<T> is unstripped into broken managed code by
            // Il2CppInterop, so read Unity's descriptor store directly.
            // The game never touches XR, so Unity never built the scripting class map that lets native
            // subsystem descriptors get managed counterparts. Building it materialises them.
            if ((UnityEngine.SubsystemsImplementation.SubsystemDescriptorStore.s_IntegratedDescriptors?.Count ?? 0) == 0)
                SubsystemManager.StaticConstructScriptingClassMap();
            var all = UnityEngine.SubsystemsImplementation.SubsystemDescriptorStore.s_IntegratedDescriptors;
            XRDisplaySubsystemDescriptor desc = null;
            Plugin.Logger.LogInfo($"Integrated descriptors: {(all == null ? -1 : all.Count)}");
            for (int i = 0; all != null && i < all.Count; i++)
            {
                Plugin.Logger.LogInfo($"  descriptor {all[i].id} ({all[i].GetIl2CppType().FullName})");
                var d = all[i].TryCast<XRDisplaySubsystemDescriptor>();
                if (d == null) continue;
                Plugin.Logger.LogInfo($"XR display descriptor: {d.id}");
                if (d.id == DisplayId) desc = d;
            }
            if (desc == null)
            {
                Plugin.Logger.LogError("OpenVR display descriptor not found (UnitySubsystemsManifest.json missing?).");
                return false;
            }

            Display = desc.Create();
            var init = GetInitializationResult();
            if (Display == null || init != EVRInitError.None)
            {
                Plugin.Logger.LogError($"OpenVR initialisation failed: {init}");
                Display?.Destroy();
                Display = null;
                return false;
            }
            // The display's render-thread frame setup calls into the input provider
            // (OpenVRInputProvider::GfxThread_UpdateDevices) without a null check, so the input
            // subsystem must exist and run before the display starts.
            for (int i = 0; all != null && i < all.Count; i++)
            {
                var d = all[i].TryCast<XRInputSubsystemDescriptor>();
                if (d == null || d.id != InputId) continue;
                Input = d.Create();
                Input?.Start();
            }
            if (Input == null)
            {
                Plugin.Logger.LogError("OpenVR input subsystem missing; starting the display without it would crash.");
                Display.Destroy();
                Display = null;
                return false;
            }
            Display.Start();

            var inputErr = OpenVR.Input.SetActionManifestPath(manifest);
            if (inputErr != EVRInputError.None && inputErr != EVRInputError.MismatchedActionManifest)
                Plugin.Logger.LogWarning($"SetActionManifestPath: {inputErr} ({manifest})");
            if (!VRInput.Init()) Plugin.Logger.LogWarning("SteamVR input did not initialise; controllers will not work.");

            XRSettings.eyeTextureResolutionScale = Mathf.Clamp(VRConfig.RenderScale.Value, 0.5f, 2f);
            Enabled = true;
            Plugin.Logger.LogInfo($"VR started: {XRSettings.loadedDeviceName} eye texture {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} " +
                                  $"refresh {OpenVR.System.GetFloatTrackedDeviceProperty(0, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref _propErr)}Hz");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("Failed to start VR: " + e);
            Enabled = false;
            return false;
        }
    }

    static ETrackedPropertyError _propErr;
    public static void Stop()
    {
        if (!Enabled) return;
        Enabled = false;
        try
        {
            Input?.Stop();
            Input?.Destroy();
            Display?.Stop();
            Display?.Destroy();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("Error stopping VR: " + e.Message);
        }
        Display = null;
    }

    public static void SetMirror(int mode)
    {
        if (Enabled) SetMirrorViewMode((ushort)Mathf.Clamp(mode, 0, 3));
    }
}
