using System;
using System.Runtime.InteropServices;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace ShroomVR;

/// <summary>
/// A dedicated Input System keyboard the controllers press keys on. The game's menus listen to the UI
/// input module's Cancel/Submit actions, which are bound to Escape/Enter; a keyboard keeps the game in
/// keyboard-and-mouse mode (a virtual gamepad would flip it into pad focus navigation and hide the
/// pointer).
/// </summary>
public static class VirtualKeyboard
{
    const int HeaderSize = 24; // InputEvent (20) + StateEvent.stateFormat (4)
    static readonly int FourCCState = FourCC('S', 'T', 'A', 'T');
    static readonly int FourCCKeys = FourCC('K', 'E', 'Y', 'S');

    static Keyboard _device;
    static IntPtr _buffer;
    static int _stateBytes;
    static readonly bool[] _keys = new bool[256];
    static bool _dirty;

    static int FourCC(char a, char b, char c, char d) => (a << 24) | (b << 16) | (c << 8) | d;

    static bool Ensure()
    {
        if (_device != null && _device.added)
        {
            if (!_device.enabled) InputSystem.EnableDevice(_device);
            return true;
        }
        try
        {
            _device = InputSystem.AddDevice("Keyboard", "ShroomVR Controller Keys").Cast<Keyboard>();
            _stateBytes = (int)((_device.stateBlock.sizeInBits + 7) / 8);
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal(HeaderSize + _stateBytes);
            _dirty = true;
            Plugin.Logger.LogInfo($"Controller keyboard added (id {_device.deviceId}, {_stateBytes} state bytes)");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("Could not create controller keyboard: " + e);
            _device = null;
            return false;
        }
    }

    public static void Set(Key key, bool down)
    {
        int k = (int)key;
        if (k <= 0 || k >= _keys.Length || _keys[k] == down) return;
        _keys[k] = down;
        _dirty = true;
    }

    /// <summary>Writes the key state when it changed. Call once per frame.</summary>
    public static unsafe void Flush()
    {
        if (!_dirty || !Ensure()) return;
        _dirty = false;

        byte* p = (byte*)_buffer;
        int size = HeaderSize + _stateBytes;
        new Span<byte>(p, size).Clear();
        *(int*)(p + 0) = FourCCState;
        *(ushort*)(p + 4) = (ushort)size;
        *(ushort*)(p + 6) = (ushort)_device.deviceId;
        *(double*)(p + 12) = InputState.currentTime;
        *(int*)(p + 20) = FourCCKeys;
        byte* bits = p + HeaderSize;
        for (int k = 1; k < _keys.Length && (k >> 3) < _stateBytes; k++)
            if (_keys[k]) bits[k >> 3] |= (byte)(1 << (k & 7));

        // Immediate change, as for the virtual mouse (queued state events lose bits on this build).
        InputState.Change(_device, InputEventPtr.From((InputEvent*)p));
    }
}
