using System;
using ControllerNav;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ShroomVR;

/// <summary>
/// Both thumbsticks and the d-pad navigate with the game's own gamepad focus: the hand of cards,
/// targets, the deck view, the map and menus. The game already switches between its mouse and gamepad
/// modes by whichever was used last; VR does the same. Navigating puts the game in gamepad mode (the
/// laser goes dark and focus starts on whatever it pointed at), and using the laser again (moving the
/// hand, pulling the trigger) puts it back in mouse mode.
/// </summary>
public static class StickNav
{
    const float StickEnter = 0.5f;        // stick deflection that starts navigating
    const float StickRelease = 0.3f;
    // Aiming again hands control back to the laser: a quick turn or move of the laser hand. The rest
    // pose follows the hand slowly, so drift while reading a card doesn't count.
    const float LaserMoveAngle = 4f;
    const float LaserMoveDistance = 0.05f;
    const float RestFollowTime = 0.5f;

    /// <summary>The game is in gamepad mode because of the sticks or d-pad.</summary>
    public static bool Active { get; private set; }

    /// <summary>Navigation input is held back until released (the press that switched modes).</summary>
    public static bool Masked { get; private set; }

    static BasicInputSource _source;
    static Transform _handover;
    static int _handoverUntil;
    static Hand _navHand = Hand.Left;
    static IntPtr _subscribed;
    static Il2CppSystem.Action<Il2CppSystem.Object> _onFocusMoved;
    static Quaternion _aimRot;
    static Vector3 _aimPos;
    static Hand _aimHand;

    /// <summary>Both sticks combined: the one pushed further. The right stick's sideways push belongs to turning when that is on.</summary>
    public static Vector2 Stick
    {
        get
        {
            var l = VRInput.GetStick(Hand.Left);
            var r = VRInput.GetStick(Hand.Right);
            if (VRConfig.Turn.Value != TurnMode.Off) r.x = 0;
            return r.sqrMagnitude > l.sqrMagnitude ? r : l;
        }
    }

    static bool DPadHeld => VRInput.GetAny(VRAction.NavUp) || VRInput.GetAny(VRAction.NavDown) ||
                            VRInput.GetAny(VRAction.NavLeft) || VRInput.GetAny(VRAction.NavRight) ||
                            VRInput.GetAny(VRAction.PagePrev) || VRInput.GetAny(VRAction.PageNext);

    static bool DPadDown => VRInput.GetDownAny(VRAction.NavUp) || VRInput.GetDownAny(VRAction.NavDown) ||
                            VRInput.GetDownAny(VRAction.NavLeft) || VRInput.GetDownAny(VRAction.NavRight) ||
                            VRInput.GetDownAny(VRAction.PagePrev) || VRInput.GetDownAny(VRAction.PageNext);

    /// <summary>Called once per frame by <see cref="VRFrame"/>, after VRInput has polled and before the laser.</summary>
    public static void Update()
    {
        try
        {
            if (Masked && !DPadHeld && Stick.magnitude < StickRelease) Masked = false;

            bool gamePad = GameInput.IsUsingGamepad;
            if (Active && !gamePad)
            {
                // Something else (the desk mouse or keyboard) took the game back.
                Active = false;
                Masked = false;
            }

            TrackNavHand();
            if (Active)
            {
                if (_handover != null) Handover();
                if (DPadHeld || Stick.magnitude >= StickRelease) RememberAim();
                else if (LaserUsed()) Exit();
                else FollowAim();
            }
            else if (!gamePad && WantsToNavigate())
            {
                Enter();
            }
        }
        catch (Exception e)
        {
            VRRig.LogOnce("stick navigation", e);
        }
    }

    /// <summary>The controller the last navigation came from, for the focus tick.</summary>
    static void TrackNavHand()
    {
        for (int h = 0; h < 2; h++)
        {
            var hand = (Hand)h;
            if (VRInput.GetDown(VRAction.NavUp, hand) || VRInput.GetDown(VRAction.NavDown, hand) ||
                VRInput.GetDown(VRAction.NavLeft, hand) || VRInput.GetDown(VRAction.NavRight, hand) ||
                VRInput.GetDown(VRAction.PagePrev, hand) || VRInput.GetDown(VRAction.PageNext, hand))
                _navHand = hand;
        }
        var l = VRInput.GetStick(Hand.Left).magnitude;
        var r = VRInput.GetStick(Hand.Right).magnitude;
        if (Mathf.Max(l, r) >= StickRelease) _navHand = r > l ? Hand.Right : Hand.Left;
    }

    /// <summary>A light tick in the navigating hand whenever the game's focus moves, like the laser's hover tick.</summary>
    static void Subscribe(S_ControllerFocus focus)
    {
        if (focus.Pointer == _subscribed) return;
        _subscribed = focus.Pointer;
        _onFocusMoved ??= (Action<Il2CppSystem.Object>)(_ =>
        {
            if (Active) VRInput.Haptic(_navHand, 0.01f, 160f, 0.15f * VRConfig.HapticStrength.Value);
        });
        focus.add_OnFocusMoved(_onFocusMoved);
    }

    static bool WantsToNavigate()
    {
        if (DPadDown) return true;
        if (Stick.magnitude < StickEnter) return false;
        // Where the game lets the stick walk (tunnels, a cleared room) it keeps walking, laser and all.
        if (!S_ControllerFocus.TryGetFocus(out var focus) || focus == null) return true;
        var player = focus._player;
        return player == null || !StickOwnership.Walks(focus.Mode, player);
    }

    static bool LaserUsed()
    {
        if (VRInput.GetDownAny(VRAction.Click)) return true;
        var laser = Laser.Instance;
        if (laser == null || VRRig.Instance == null) return false;
        if (laser.ActiveHand != _aimHand) return true;
        var aim = Aim(_aimHand);
        if (aim == null) return false;
        return Quaternion.Angle(aim.localRotation, _aimRot) > LaserMoveAngle ||
               (aim.localPosition - _aimPos).magnitude > LaserMoveDistance;
    }

    static void FollowAim()
    {
        var aim = Aim(_aimHand);
        if (aim == null) return;
        float t = 1f - Mathf.Exp(-Time.unscaledDeltaTime / RestFollowTime);
        _aimRot = Quaternion.Slerp(_aimRot, aim.localRotation, t);
        _aimPos = Vector3.Lerp(_aimPos, aim.localPosition, t);
    }

    static Transform Aim(Hand h)
    {
        var rig = h == Hand.Left ? VRRig.Instance?.Left : VRRig.Instance?.Right;
        return rig != null && rig.Tracked ? rig.Aim : null;
    }

    static void RememberAim()
    {
        _aimHand = Laser.Instance != null ? Laser.Instance.ActiveHand : Hand.Right;
        var aim = Aim(_aimHand);
        if (aim == null) return;
        _aimRot = aim.localRotation;
        _aimPos = aim.localPosition;
    }

    static BasicInputSource Source()
    {
        if (_source == null) _source = UnityEngine.Object.FindObjectOfType<BasicInputSource>();
        return _source;
    }

    static void Enter()
    {
        var source = Source();
        if (source == null) return;
        Active = true;
        Masked = true;
        RememberAim();
        _handover = PointedAt();
        _handoverUntil = Time.frameCount + 15;
        // The game's own switch. Its focus then starts from the menu button under the pointer; cards
        // under the laser are handed over below.
        source.SetInputContext(InputDeviceType.Gamepad, GamepadFamily.Xbox);
        if (S_ControllerFocus.TryGetFocus(out var focus) && focus != null) Subscribe(focus);
    }

    /// <summary>What the pointer is over, as the game's event system sees it (cards and UI).</summary>
    static Transform PointedAt()
    {
        var es = EventSystem.current;
        if (es == null || Laser.Instance == null || !Laser.Instance.HasTarget) return null;
        var ped = new PointerEventData(es) { position = Laser.Instance.ScreenPoint };
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        es.RaycastAll(ped, results);
        return results.Count > 0 && results[0].gameObject != null ? results[0].gameObject.transform : null;
    }

    /// <summary>Once the game's focus is up, start it on what the laser pointed at.</summary>
    static void Handover()
    {
        if (Time.frameCount > _handoverUntil || !S_ControllerFocus.TryGetFocus(out var focus) || focus == null)
        {
            _handover = null;
            return;
        }
        bool done = focus.Mode switch
        {
            FocusMode.Hand => FocusInHand(focus.HandResolver, _handover),
            FocusMode.List => FocusInList(focus.OrderedResolver, _handover),
            FocusMode.Mixed => FocusInGrid(focus.MixedResolver, _handover),
            _ => false,
        };
        if (done) _handover = null;
    }

    static bool Is(Il2CppSystem.Object item, Transform pointed)
    {
        var c = item?.TryCast<Component>();
        return c != null && pointed.IsChildOf(c.transform);
    }

    static bool FocusInHand(HandFocusResolver resolver, Transform pointed)
    {
        var hand = resolver?.ResolveHand();
        if (hand == null) return false;
        for (int i = 0; i < hand.FocusableCardCount; i++)
        {
            if (!Is(hand.GetFocusableCard(i)?.Cast<Il2CppSystem.Object>(), pointed)) continue;
            if (hand.ControllerFocusedIndex.HasValue && hand.ControllerFocusedIndex.Value == i) return true;
            resolver.SetFocus(i);
            return true;
        }
        _handover = null; // pointing at something that isn't in the hand
        return false;
    }

    static bool FocusInList(OrderedFocusResolver resolver, Transform pointed)
    {
        if (resolver == null) return false;
        for (int s = 0; s < resolver.SectionCount; s++)
        {
            var section = resolver.SectionAt(s);
            if (section == null) continue;
            for (int i = 0; i < section.Count; i++)
            {
                if (!Is(section.ItemAt(i), pointed)) continue;
                resolver._section = s;
                resolver.SetFocus(i, "VR laser handover", true);
                return true;
            }
        }
        _handover = null;
        return false;
    }

    static bool FocusInGrid(MixedFocusResolver resolver, Transform pointed)
    {
        if (resolver == null) return false;
        for (int s = 0; s < resolver.SectionCount; s++)
        {
            var section = resolver.SectionAt(s);
            if (section == null) continue;
            for (int i = 0; i < section.Count; i++)
            {
                if (!Is(section.ItemAt(i), pointed)) continue;
                resolver._section = s;
                resolver.SetFocus(i, "VR laser handover", true);
                return true;
            }
        }
        _handover = null;
        return false;
    }

    static void Exit()
    {
        Active = false;
        Masked = false;
        _handover = null;
        var source = Source();
        if (source != null && GameInput.IsUsingGamepad) source.SetInputContext(InputDeviceType.KeyboardMouse, GamepadFamily.Unknown);
    }

    /// <summary>Leave gamepad mode (e.g. when VR stops).</summary>
    public static void Reset()
    {
        if (Active) Exit();
    }
}
