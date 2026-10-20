using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace ShroomVR;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "com.shroomvr.shroomandgloom";
    public const string Name = "Shroom and Gloom VR";
    public const string Version = BuildInfo.Version;

    internal static ManualLogSource Logger;
    internal static Plugin Instance;
    internal static Harmony Harmony;

    public override void Load()
    {
        Instance = this;
        Logger = Log;
        Log.LogInfo($"{Name} {Version} loading");

        VRConfig.Bind(Config);
        ClassInjector.RegisterTypeInIl2Cpp<VRRig>();
        ControllerModel.Register();

        try
        {
            var harmony = Harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Plugin).Assembly);
            PatchSafety.Verify(harmony);
            int n = 0;
            foreach (var _ in harmony.GetPatchedMethods()) n++;
            Log.LogInfo($"Patched {n} game methods");
        }
        catch (Exception e)
        {
            Log.LogError("Harmony patching failed: " + e);
        }

        if (VRConfig.DevConsole.Value) AddComponent<DevConsole>();
        AddComponent<Bootstrap>();
    }
}

/// <summary>Starts VR on the first frame, once the engine is fully up.</summary>
public class Bootstrap : MonoBehaviour
{
    public Bootstrap(IntPtr ptr) : base(ptr) { }

    static int _origWidth, _origHeight;
    static FullScreenMode _origMode;
    static bool _resized;

    void Start()
    {
        if (VRConfig.DisableCrashReporting.Value)
        {
            VRSystem.InstallResolver();
            CrashReporting.Disable();
        }

        if (!VRSystem.ShouldStart() || !VRSystem.Start())
        {
            Plugin.Logger.LogInfo("Running flat (no VR).");
            return;
        }

        Application.runInBackground = true;
        // In the headset the desktop window rarely has focus; input must keep flowing anyway.
        UnityEngine.InputSystem.InputSystem.settings.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
        if (!Debug.Skip("res")) ApplyWindowResolution();
        if (Environment.GetEnvironmentVariable("SHROOMVR_NORIG") == "1")
        {
            Plugin.Logger.LogWarning("SHROOMVR_NORIG set: XR display only, no camera rig (diagnostics)");
            Laser.Create(); // console pointer injection still works
            return;
        }
        VRUI.Init();
        VRRig.Create();
        Laser.Create();
    }

    static int _vrWidth, _vrHeight;
    float _nextResCheck;

    /// <summary>The game re-applies its saved display settings after boot; keep the VR window size.</summary>
    void Update()
    {
        if (_vrWidth == 0 || Time.unscaledTime < _nextResCheck) return;
        _nextResCheck = Time.unscaledTime + 1f;
        if (Screen.width != _vrWidth || Screen.height != _vrHeight)
        {
            Plugin.Logger.LogInfo($"Window changed to {Screen.width}x{Screen.height}; restoring {_vrWidth}x{_vrHeight} for VR");
            Screen.SetResolution(_vrWidth, _vrHeight, FullScreenMode.ExclusiveFullScreen);
        }
    }

    static void ApplyWindowResolution()
    {
        var spec = VRConfig.WindowResolution.Value?.Trim();
        if (string.IsNullOrEmpty(spec)) return;
        var parts = spec.ToLowerInvariant().Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h) || w < 320 || h < 200) return;
        _vrWidth = w;
        _vrHeight = h;
        if (Screen.width == w && Screen.height == h) return;
        _origWidth = Screen.width;
        _origHeight = Screen.height;
        _origMode = Screen.fullScreenMode;
        _resized = true;
        // Exclusive mode survives tiling window managers (a windowed request gets resized back to the
        // monitor); the desktop just shows a scaled mirror.
        Screen.SetResolution(w, h, FullScreenMode.ExclusiveFullScreen);
        Plugin.Logger.LogInfo($"Window {_origWidth}x{_origHeight} {_origMode} -> {w}x{h} for VR (restored on exit)");
    }

    void OnApplicationQuit()
    {
        if (_resized) Screen.SetResolution(_origWidth, _origHeight, _origMode);
        VRSystem.Stop();
    }
}
