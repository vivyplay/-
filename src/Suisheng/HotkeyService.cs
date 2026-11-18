using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Suisheng;

public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource source;
    private int activeId = 101;
    private uint modifiers, key;
    private bool registered, escape, holdMode, holdActive;
    private readonly Func<int, short> keyState;
    private readonly DispatcherTimer holdTimer;
    public bool Paused { get; private set; }
    public event Action? Released;
    public event Action? Triggered;
    public event Action? Cancelled;
    public event Action? ShowRequested;
    public static uint ActivateMessage { get; } = NativeMethods.RegisterWindowMessage("Suisheng.Activate.0.1");

    public HotkeyService(Func<int, short>? keyStateReader = null)
    {
        keyState = keyStateReader ?? NativeMethods.GetAsyncKeyState;
        source = new HwndSource(new HwndSourceParameters("Suisheng.Hotkey") { WindowStyle = 0, Width = 0, Height = 0 });
        source.AddHook(Hook);
        holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(35) };
        holdTimer.Tick += (_, _) => CheckRelease();
    }

    public void SetRecordingMode(bool hold)
    {
        holdMode = hold;
        holdActive = false;
        holdTimer.Stop();
    }

    private void CheckRelease()
    {
        if (!holdActive || IsPressed(modifiers, key)) return;
        holdActive = false;
        holdTimer.Stop();
        Released?.Invoke();
    }

    private bool IsPressed(uint modifiers, uint key)
    {
        if ((keyState((int)key) & 0x8000) == 0) return false;
        if ((modifiers & 2) != 0 && (keyState(0x11) & 0x8000) == 0) return false;
        if ((modifiers & 1) != 0 && (keyState(0x12) & 0x8000) == 0) return false;
        if ((modifiers & 4) != 0 && (keyState(0x10) & 0x8000) == 0) return false;
        if ((modifiers & 8) != 0 && (keyState(0x5B) & 0x8000) == 0 && (keyState(0x5C) & 0x8000) == 0) return false;
        return true;
    }

    public bool TrySet(uint newModifiers, uint newKey)
    {
        if (registered && modifiers == newModifiers && key == newKey && !Paused) return true;
        int nextId = activeId == 101 ? 102 : 101;
        if (!NativeMethods.RegisterHotKey(source.Handle, nextId, newModifiers | 0x4000, newKey)) return false;
        if (registered) NativeMethods.UnregisterHotKey(source.Handle, activeId);
        activeId = nextId;
        modifiers = newModifiers;
        key = newKey;
        registered = true;
        Paused = false;
        return true;
    }

    public bool TogglePause()
    {
        if (Paused) return TrySet(modifiers, key);
        if (registered) NativeMethods.UnregisterHotKey(source.Handle, activeId);
        registered = false;
        Paused = true;
        return true;
    }

    public bool SetTaskActive(bool active)
    {
        if (escape) NativeMethods.UnregisterHotKey(source.Handle, 103);
        escape = active && NativeMethods.RegisterHotKey(source.Handle, 103, 0x4000, 0x1B);
        return !active || escape;
    }

    private nint Hook(nint hwnd, int message, nint wparam, nint lparam, ref bool handled)
    {
        if ((uint)message == ActivateMessage) { ShowRequested?.Invoke(); handled = true; }
        if (message == NativeMethods.WM_HOTKEY)
        {
            if (wparam == activeId && !Paused)
            {
                if (holdMode)
                {
                    if (!holdActive)
                    {
                        holdActive = true;
                        holdTimer.Start();
                        Triggered?.Invoke();
                    }
                }
                else Triggered?.Invoke();
            }
            if (wparam == 103) Cancelled?.Invoke();
            handled = true;
        }
        return 0;
    }

    public static string Format(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        parts.Add(KeyInterop.KeyFromVirtualKey((int)key).ToString());
        return string.Join(" + ", parts);
    }

    public void Dispose()
    {
        SetTaskActive(false);
        holdActive = false;
        holdTimer.Stop();
        if (registered) NativeMethods.UnregisterHotKey(source.Handle, activeId);
        source.RemoveHook(Hook);
        source.Dispose();
    }
}
