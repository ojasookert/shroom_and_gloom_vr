using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace ShroomVR;

/// <summary>
/// A dedicated Input System mouse driven by the VR laser pointer. Writing raw MouseState events
/// keeps the game's UI module, its EventSystem raycasters and GameInput all in agreement about
/// where the pointer is, without touching game code.
/// </summary>
public static class VirtualMouse
{
    // InputEvent header (20 bytes) + StateEvent.stateFormat (4) + MouseState (30).
    const int HeaderSize = 24;
    const int MouseStateSize = 30;
    const int EventSize = HeaderSize + MouseStateSize;
    static readonly int FourCCState = FourCC('S', 'T', 'A', 'T');
    static readonly int FourCCMouse = FourCC('M', 'O', 'U', 'S');

    static Mouse _device;
    static IntPtr _buffer;
    static Vector2 _position;
    static ushort _buttons;
    static Vector2 _pendingScroll;
    static int _debugLog = 5;
    public static bool UseImmediateChange = true; // queued events lose their button bits on this build
    /// <summary>When false the device stays idle so the real mouse keeps working.</summary>
    public static bool Active;
    public static bool UseOwnDevice = true;
    public static void Reset() => _device = null;

    public static Mouse Device => _device;

    public static unsafe string DebugBytes()
    {
        if (_device == null || _buffer == IntPtr.Zero) return "-";
        byte* ev = (byte*)_buffer;
        byte* st = (byte*)_device.currentStatePtr + _device.stateBlock.byteOffset;
        string Hex(byte* p, int n) { var sb = new System.Text.StringBuilder(); for (int i = 0; i < n; i++) sb.Append(p[i].ToString("x2")).Append(i % 4 == 3 ? " " : ""); return sb.ToString(); }
        return $"event: {Hex(ev, EventSize)}\nstate(off={_device.stateBlock.byteOffset} size={_device.stateBlock.sizeInBits / 8} fmt={_device.stateBlock.format}): {Hex(st, 32)}";
    }
    public static Vector2 Position => _position;
    public static bool LeftDown => (_buttons & 1) != 0;
    public static bool RightDown => (_buttons & 2) != 0;

    static int FourCC(char a, char b, char c, char d) => (a << 24) | (b << 16) | (c << 8) | d;

    public static bool Ensure()
    {
        if (_device != null && _device.added)
        {
            // Focus changes reset and disable non-native devices; ours has no backend to re-enable it.
            if (!_device.enabled) InputSystem.EnableDevice(_device);
            return true;
        }
        try
        {
            // The game pairs its actions with the system keyboard and mouse, so a second mouse device
            // would be ignored. Drive the existing one; nobody uses the desk mouse while in the headset.
            if (!UseOwnDevice) foreach (var d in InputSystem.devices.ToArray())
            {
                var m = d.TryCast<Mouse>();
                if (m != null && m.name != "ShroomVR Laser Mouse") { _device = m; break; }
            }
            _device ??= InputSystem.AddDevice("Mouse", "ShroomVR Laser Mouse").Cast<Mouse>();
            if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(EventSize);
            Plugin.Logger.LogInfo($"Laser drives mouse '{_device.name}' (id {_device.deviceId})");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("Could not create virtual mouse: " + e);
            _device = null;
            return false;
        }
    }

    public static void Remove() => _device = null;

    public static void SetPosition(Vector2 screenPos)
    {
        Active = true;
        if (screenPos == _position) return;
        _position = screenPos;
    }

    public static void SetButton(int button, bool down)
    {
        var bit = (ushort)(1 << button);
        var next = down ? (ushort)(_buttons | bit) : (ushort)(_buttons & ~bit);
        if (next == _buttons) return;
        _buttons = next;
    }

    public static void Scroll(Vector2 delta)
    {
        _pendingScroll += delta;
    }

    /// <summary>
    /// Queues the current state. Sent every frame while active: the first event after the device
    /// is created is dropped by the Input System, and a steady stream keeps it the current mouse.
    /// </summary>
    public static unsafe void Flush()
    {
        if (!Active || !Ensure()) return;

        byte* p = (byte*)_buffer;
        new Span<byte>(p, EventSize).Clear();
        *(int*)(p + 0) = FourCCState;               // InputEvent.type
        *(ushort*)(p + 4) = EventSize;               // InputEvent.sizeInBytes
        *(ushort*)(p + 6) = (ushort)_device.deviceId; // InputEvent.deviceId
        *(double*)(p + 12) = InputState.currentTime;  // InputEvent.time
        *(int*)(p + 20) = FourCCMouse;               // StateEvent.stateFormat
        byte* s = p + HeaderSize;                    // MouseState
        *(Vector2*)(s + 0) = _position;
        *(Vector2*)(s + 8) = Vector2.zero;           // delta: never drive mouse-look from the laser
        *(Vector2*)(s + 16) = _pendingScroll;
        *(ushort*)(s + 24) = _buttons;
        _pendingScroll = Vector2.zero;

        var evt = InputEventPtr.From((InputEvent*)p);
        if (UseImmediateChange) InputState.Change(_device, evt);
        else InputSystem.QueueEvent(evt);
        if (_debugLog-- > 0)
            Plugin.Logger.LogInfo($"VirtualMouse flush pos={_position} buttons={_buttons} valid={evt.valid} type={evt.type} size={evt.sizeInBytes} dev={evt.deviceId} enabled={_device.enabled} t={InputState.currentTime}");
    }
}
