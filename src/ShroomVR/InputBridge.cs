using System;
using HarmonyLib;
using UnityEngine;

namespace ShroomVR;

/// <summary>
/// Feeds VR controller input into the game's single input source (BasicInputSource). Every game
/// system reads actions through it, so patching it covers menus, cards, movement and End Turn.
/// </summary>
[HarmonyPatch(typeof(BasicInputSource))]
public static class InputBridge
{
    const int ActionCount = 40;
    static readonly bool[] Held = new bool[ActionCount];
    static readonly bool[] Down = new bool[ActionCount];
    static readonly bool[] Up = new bool[ActionCount];
    static Vector2 _move;
    static readonly bool[] _mouseHeld = new bool[3], _mousePrev = new bool[3];

    static readonly (InputActionId game, VRAction vr)[] Map =
    {
        (InputActionId.Confirm, VRAction.Confirm),
        (InputActionId.Cancel, VRAction.Cancel),
        (InputActionId.Pause, VRAction.Pause),
        (InputActionId.EndTurn, VRAction.EndTurn),
        (InputActionId.Inspect, VRAction.Inspect),
        (InputActionId.ToggleDeckView, VRAction.DeckView),
        (InputActionId.ToggleMap, VRAction.Map),
        (InputActionId.SortHand, VRAction.SortHand),
        (InputActionId.PagePrev, VRAction.PagePrev),
        (InputActionId.PageNext, VRAction.PageNext),
        (InputActionId.FocusPrev, VRAction.NavLeft),
        (InputActionId.FocusNext, VRAction.NavRight),
        (InputActionId.FocusUp, VRAction.NavUp),
        (InputActionId.FocusDown, VRAction.NavDown),
        (InputActionId.Run, VRAction.Run),
        (InputActionId.RecentreView, VRAction.Recenter),
    };

    /// <summary>Called once per frame by <see cref="VRFrame"/>.</summary>
    public static void Compute()
    {
        Array.Clear(Held, 0, ActionCount);
        Array.Clear(Down, 0, ActionCount);
        Array.Clear(Up, 0, ActionCount);
        foreach (var (game, vr) in Map)
        {
            int i = (int)game;
            Held[i] = VRInput.GetAny(vr);
            Down[i] = VRInput.GetDownAny(vr);
            Up[i] = VRInput.GetUpAny(vr);
        }

        // The trigger on the laser hand is the mouse button.
        var laser = Laser.Instance;
        if (laser != null)
        {
            int i = (int)InputActionId.Interact;
            Held[i] = VRInput.Get(VRAction.Click, laser.ActiveHand);
            Down[i] = VRInput.GetDown(VRAction.Click, laser.ActiveHand);
            Up[i] = VRInput.GetUp(VRAction.Click, laser.ActiveHand);
        }

        // The press that switched the game to gamepad navigation only switches; it doesn't also step.
        if (StickNav.Masked)
            foreach (var id in NavActions)
            {
                Held[(int)id] = false;
                Down[(int)id] = false;
            }

        ApplyDebug();

        // Legacy mouse buttons follow the laser too: the game decides card casts with
        // Input.GetMouseButtonUp (V_PlayerSpellCaster.HandleCastRelease).
        for (int b = 0; b < 3; b++) _mousePrev[b] = _mouseHeld[b];
        _mouseHeld[0] = VirtualMouse.LeftDown;
        _mouseHeld[1] = VirtualMouse.RightDown;
        _mouseHeld[2] = false;

        // B is "back": it closes whatever is open, the way each screen expects to be closed. While
        // navigating with the sticks the game's focus does that itself (Cancel on the focused screen).
        int cancel = (int)InputActionId.Cancel;
        if (Down[cancel] && !StickNav.Active)
        {
            var back = BackAction();
            if (back != InputActionId.None)
            {
                Down[(int)back] = true;
                Held[(int)back] = true;
            }
        }

        // Menus listen to the UI module's Cancel/Submit (Escape/Enter): B backs out, A confirms. Not in
        // gamepad navigation: the game's focus confirms and cancels, and a key press would switch the
        // game back to keyboard and mouse.
        bool keys = !StickNav.Active;
        VirtualKeyboard.Set(UnityEngine.InputSystem.Key.Escape, keys && Held[(int)InputActionId.Cancel]);
        VirtualKeyboard.Set(UnityEngine.InputSystem.Key.Enter, keys && Held[(int)InputActionId.Confirm]);
        VirtualKeyboard.Flush();

        // Both sticks walk, or step the game's focus where it doesn't let you walk.
        var stick = StickNav.Stick;
        _move = stick.magnitude < 0.15f || StickNav.Masked ? Vector2.zero : stick;
        if (_debugMoveUntil > Time.unscaledTime) _move = _debugMove;
        // Pushing the stick all the way forward runs.
        int run = (int)InputActionId.Run;
        if (_move.y > 0.95f) Held[run] = true;
    }

    static readonly InputActionId[] NavActions =
    {
        InputActionId.FocusPrev, InputActionId.FocusNext, InputActionId.FocusUp, InputActionId.FocusDown,
        InputActionId.PagePrev, InputActionId.PageNext,
    };

    static bool Active => VRSystem.Enabled;

    static S_PlayerValuesDisplay _values;
    static CardDisplayParent _deck;

    /// <summary>The action that closes the topmost open screen, or None to just send Cancel.</summary>
    static InputActionId BackAction()
    {
        try
        {
            if (_values == null) _values = UnityEngine.Object.FindObjectOfType<S_PlayerValuesDisplay>();
            if (_values != null && _values.PauseMenuIsOpen()) return InputActionId.Pause;
            if (_deck == null) _deck = UnityEngine.Object.FindObjectOfType<CardDisplayParent>();
            if (_deck != null)
            {
                var cam = _deck.GetComponentInChildren<Camera>(true);
                if (cam != null && cam.gameObject.activeInHierarchy) return InputActionId.ToggleDeckView;
            }
            if (Diegetic.MapOpen) return InputActionId.ToggleMap;
        }
        catch (Exception e)
        {
            VRRig.LogOnce("BackAction", e);
        }
        return InputActionId.None;
    }

    // ---- console-driven input for testing without controllers in hand ----
    static readonly System.Collections.Generic.Dictionary<int, int> _debugHold = new();
    static readonly System.Collections.Generic.HashSet<int> _debugWasHeld = new();
    static Vector2 _debugMove;
    static float _debugMoveUntil;

    /// <summary>Press a game action for the given number of frames.</summary>
    public static void DebugPress(InputActionId id, int frames) => _debugHold[(int)id] = Mathf.Max(1, frames);
    public static void DebugRelease(InputActionId id) => _debugHold.Remove((int)id);
    public static void DebugMove(Vector2 v, float seconds) { _debugMove = v; _debugMoveUntil = Time.unscaledTime + seconds; }
    public static bool DebugHeld(InputActionId id) => _debugHold.ContainsKey((int)id);

    static void ApplyDebug()
    {
        var keys = new System.Collections.Generic.List<int>(_debugHold.Keys);
        foreach (var k in keys)
        {
            Held[k] = true;
            if (!_debugWasHeld.Contains(k)) { Down[k] = true; _debugWasHeld.Add(k); }
            if (--_debugHold[k] <= 0) _debugHold.Remove(k);
        }
        foreach (var k in new System.Collections.Generic.List<int>(_debugWasHeld))
            if (!_debugHold.ContainsKey(k)) { _debugWasHeld.Remove(k); Up[k] = true; }
    }

    static bool Index(InputActionId id, out int i)
    {
        i = (int)id;
        return i >= 0 && i < ActionCount;
    }

    static bool MouseButton(int button, out bool held, out bool down, out bool up)
    {
        held = down = up = false;
        if (!Active || button < 0 || button > 2) return false;
        VRFrame.Tick();
        held = _mouseHeld[button];
        down = held && !_mousePrev[button];
        up = !held && _mousePrev[button];
        return true;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetMouseButton))]
    static bool GetMouseButton(int button, ref bool __result)
    {
        if (!MouseButton(button, out var held, out _, out _)) return true;
        __result = held;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetMouseButtonDown))]
    static bool GetMouseButtonDown(int button, ref bool __result)
    {
        if (!MouseButton(button, out _, out var down, out _)) return true;
        __result = down;
        return false;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetMouseButtonUp))]
    static bool GetMouseButtonUp(int button, ref bool __result)
    {
        if (!MouseButton(button, out _, out _, out var up)) return true;
        __result = up;
        return false;
    }

    [HarmonyPostfix, HarmonyPatch(nameof(BasicInputSource.GetActionDown))]
    static void GetActionDown(InputActionId actionId, ref bool __result)
    {
        if (!Active) return;
        VRFrame.Tick();
        if (Index(actionId, out var i)) __result |= Down[i];
    }

    [HarmonyPostfix, HarmonyPatch(nameof(BasicInputSource.GetAction))]
    static void GetAction(InputActionId actionId, ref bool __result)
    {
        if (!Active) return;
        VRFrame.Tick();
        if (Index(actionId, out var i)) __result |= Held[i];
    }

    [HarmonyPostfix, HarmonyPatch(nameof(BasicInputSource.GetActionUp))]
    static void GetActionUp(InputActionId actionId, ref bool __result)
    {
        if (!Active) return;
        VRFrame.Tick();
        if (Index(actionId, out var i)) __result |= Up[i];
    }

    [HarmonyPostfix, HarmonyPatch(nameof(BasicInputSource.GetAxis))]
    static void GetAxis(InputActionId actionId, ref Vector2 __result)
    {
        if (!Active)
        {
            // Console-driven walking also works in the flat game, for side-by-side comparisons.
            if (actionId == InputActionId.Move && _debugMoveUntil > Time.unscaledTime) __result = _debugMove;
            return;
        }
        VRFrame.Tick();
        switch (actionId)
        {
            case InputActionId.Move:
                if (_move != Vector2.zero) __result = _move;
                break;
            case InputActionId.Look:
                __result = Vector2.zero;
                break;
        }
    }

    /// <summary>
    /// In VR the laser is the only pointer. The game's pointer getter (BasicInputSource.PointerPosition)
    /// just returns legacy Input.mousePosition, and its native body is shared with Mouse.get_Position
    /// and UGUI's BaseInput.get_mousePosition, so it cannot be patched safely. Input.mousePosition is
    /// patched instead: every legacy pointer consumer then sees the laser.
    /// </summary>
    [HarmonyPrefix, HarmonyPatch(typeof(UnityEngine.Input), nameof(UnityEngine.Input.mousePosition), MethodType.Getter)]
    static bool MousePosition(ref Vector3 __result)
    {
        if (!Active) return true;
        VRFrame.Tick();
        var laser = Laser.Instance;
        var p = laser != null ? laser.ScreenPoint : new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        __result = new Vector3(p.x, p.y, 0);
        return false;
    }
}
