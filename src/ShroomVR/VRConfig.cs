using BepInEx.Configuration;

namespace ShroomVR;

public enum VRModeSetting { Auto, On, Off }
public enum TurnMode { Snap, Smooth, Off }
public enum UiAnchor { Body, Head }

/// <summary>All user-facing settings, stored in BepInEx/config/com.shroomvr.shroomandgloom.cfg.</summary>
public static class VRConfig
{
    public static ConfigEntry<VRModeSetting> Mode;
    public static ConfigEntry<string> WindowResolution;
    public static ConfigEntry<int> MirrorEye;
    public static ConfigEntry<float> RenderScale;

    public static ConfigEntry<float> UiDistance;
    public static ConfigEntry<UiAnchor> UiFollow;
    public static ConfigEntry<float> UiFollowSpeed;
    public static ConfigEntry<float> UiFollowThreshold;

    public static ConfigEntry<TurnMode> Turn;
    public static ConfigEntry<float> SnapTurnAngle;
    public static ConfigEntry<float> SmoothTurnSpeed;
    public static ConfigEntry<bool> LeftHanded;
    public static ConfigEntry<float> WorldScale;
    public static ConfigEntry<float> HeightOffset;

    public static ConfigEntry<bool> DisableCameraShake;
    public static ConfigEntry<bool> DisableViewBob;
    public static ConfigEntry<bool> NoRoomLift;
    public static ConfigEntry<bool> HapticsFromShake;
    public static ConfigEntry<float> HapticStrength;

    public static ConfigEntry<bool> ShowControllers;
    public static ConfigEntry<float> LaserWidth;
    public static ConfigEntry<bool> MapInHand;
    public static ConfigEntry<float> MapScale;
    public static ConfigEntry<bool> CardsInHand;
    public static ConfigEntry<float> CardHandScale;
    public static ConfigEntry<bool> HoldCastCard;
    public static ConfigEntry<float> HeldCardScale;
    public static ConfigEntry<float> TriggerThreshold;

    public static ConfigEntry<bool> DisableCrashReporting;
    public static ConfigEntry<bool> DevConsole;

    public static ConfigFile File { get; private set; }

    public static void Bind(ConfigFile cfg)
    {
        File = cfg;
        Mode = cfg.Bind("General", "VRMode", VRModeSetting.Auto,
            "Auto: start in VR when SteamVR reports a headset. On/Off force it. The launch options -shroomvr-vr / -shroomvr-novr override this.");
        WindowResolution = cfg.Bind("General", "WindowResolution", "1920x1080",
            "Desktop window size used while in VR (the game lays its UI out to the window aspect; 16:9 keeps cards within comfortable view). Empty = leave the game's resolution alone.");
        MirrorEye = cfg.Bind("General", "MirrorEye", 2, "Desktop mirror: 0 none, 1 left eye, 2 right eye, 3 SteamVR's distorted view.");

        RenderScale = cfg.Bind("General", "RenderScale", 1f, "Eye texture resolution multiplier on top of SteamVR's own resolution setting (0.5 - 2).");

        UiDistance = cfg.Bind("UI", "Distance", 1.4f, "Distance in metres of the virtual screen that holds the game's HUD and menus.");
        UiFollow = cfg.Bind("UI", "Anchor", UiAnchor.Body,
            "Body: the HUD, hand of cards and menus stay in front of your body. Head: they lazily follow where you look.");
        UiFollowSpeed = cfg.Bind("UI", "FollowSpeed", 2.5f, "How fast the UI catches up in Head anchor mode.");
        UiFollowThreshold = cfg.Bind("UI", "FollowThreshold", 25f, "Degrees you can look away before the UI starts following (Head anchor mode).");

        Turn = cfg.Bind("Comfort", "TurnMode", TurnMode.Off,
            "Right stick turning. Off by default: the game turns you to face every encounter and nothing happens behind you, so the sticks navigate instead (recenter faces you forward). Snap/Smooth give the right stick's sideways push to turning.");
        SnapTurnAngle = cfg.Bind("Comfort", "SnapTurnAngle", 30f, "Degrees per snap turn.");
        SmoothTurnSpeed = cfg.Bind("Comfort", "SmoothTurnSpeed", 90f, "Degrees per second for smooth turning.");
        LeftHanded = cfg.Bind("Comfort", "LeftHanded", false, "Start with the laser on the left hand (either trigger also grabs the laser).");
        WorldScale = cfg.Bind("Comfort", "WorldScale", 1f, "Scales your size relative to the world (>1 makes the world feel smaller).");
        HeightOffset = cfg.Bind("Comfort", "HeightOffset", 0f, "Extra eye height in metres added after recentering.");

        DisableCameraShake = cfg.Bind("Comfort", "DisableCameraShake", true, "Turn screen shake off while in VR (it is turned into controller rumble instead).");
        DisableViewBob = cfg.Bind("Comfort", "DisableViewBob", true, "Turn walking head-bob off while in VR.");
        NoRoomLift = cfg.Bind("Comfort", "NoRoomLift", true, "Keep your eye height when an encounter starts (the game glides the player to a viewing spot ~0.6 m higher in rooms, camps and combats).");
        HapticsFromShake = cfg.Bind("Comfort", "HapticsFromShake", true, "Rumble the controllers when the game would shake the camera.");
        HapticStrength = cfg.Bind("Comfort", "HapticStrength", 1f, "Multiplier for all controller rumble.");

        ShowControllers = cfg.Bind("Hands", "ShowControllers", true, "Render your controllers.");
        MapInHand = cfg.Bind("Hands", "MapInHand", true, "The map is held in your left hand when open, instead of floating in front of your face.");
        MapScale = cfg.Bind("Hands", "MapScale", 1.6f, "Size of the map in your hand.");
        CardsInHand = cfg.Bind("Hands", "CardsInHand", true, "Hold your hand of cards in the off hand (left, or right when LeftHanded) instead of along the bottom of your view.");
        CardHandScale = cfg.Bind("Hands", "CardHandScale", 0.33f, "Size of the hand of cards you hold, relative to the game's layout.");
        HoldCastCard = cfg.Bind("Hands", "HoldCastCard", true, "A card you are playing is held in the laser hand instead of the game's dotted cast line.");
        HeldCardScale = cfg.Bind("Hands", "HeldCardScale", 0.35f, "Size of the card you are playing, relative to the game's.");
        TriggerThreshold = cfg.Bind("Hands", "TriggerThreshold", 0.5f, "How far the trigger (or grip) is pulled before it clicks, 0..1. It lets go a little below that.");
        LaserWidth = cfg.Bind("Hands", "LaserWidth", 0.004f, "Laser beam width in metres.");

        DisableCrashReporting = cfg.Bind("Misc", "DisableCrashReporting", true,
            "Stop the game's Sentry crash reporter while modded, so modded crashes don't end up in the developers' reports.");
        DevConsole = cfg.Bind("Misc", "DevConsole", false, "Enable the file-driven developer console (BepInEx/shroomvr_cmd.txt).");
    }
}
