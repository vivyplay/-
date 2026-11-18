using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace Suisheng;

public sealed record InputOutcome(bool Submitted, string Message);

public sealed class InputTarget
{
    public nint Window { get; init; }
    public uint Process { get; init; }
    public nint Focus { get; init; }
    public NativeCaret Caret { get; init; }
    public int[] RuntimeId { get; init; } = [];
    public AutomationElement Element { get; init; } = null!;
    public TextPatternRange? Selection { get; init; }
    public bool Changed { get; set; }
}

public readonly record struct NativeCaret(int Left, int Top, int Right, int Bottom);

public sealed class TargetInputService : IDisposable
{
    private readonly NativeMethods.WinEventDelegate callback;
    private readonly List<nint> hooks = [];
    private InputTarget? tracking;
    private long revision;
    public InputTarget? Current => tracking;

    public TargetInputService()
    {
        callback = OnEvent;
        foreach (uint type in new[] { NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_OBJECT_FOCUS,
            NativeMethods.EVENT_OBJECT_LOCATIONCHANGE, NativeMethods.EVENT_OBJECT_TEXTSELECTIONCHANGED, NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH })
        {
            nint handle = NativeMethods.SetWinEventHook(type, type, 0, callback, 0, 0, 0);
            if (handle != 0) hooks.Add(handle);
        }
    }

    private void OnEvent(nint hook, uint type, nint window, int objectId, int childId, uint thread, uint time)
    {
        if (type == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE && objectId != -8) return;
        if (type == NativeMethods.EVENT_SYSTEM_FOREGROUND || type == NativeMethods.EVENT_OBJECT_FOCUS || type == NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH)
            Interlocked.Increment(ref revision);
        var target = tracking;
        if (target == null) return;
        NativeMethods.GetWindowThreadProcessId(window, out uint process);
        if (type == NativeMethods.EVENT_SYSTEM_DESKTOPSWITCH ||
            (type == NativeMethods.EVENT_SYSTEM_FOREGROUND && window != target.Window) ||
            (process == target.Process && (type == NativeMethods.EVENT_OBJECT_FOCUS || type == NativeMethods.EVENT_OBJECT_TEXTSELECTIONCHANGED || type == NativeMethods.EVENT_OBJECT_LOCATIONCHANGE)))
            target.Changed = true;
    }

    private static NativeMethods.GuiThreadInfo ThreadInfo(nint window)
    {
        uint thread = NativeMethods.GetWindowThreadProcessId(window, out _);
        var info = new NativeMethods.GuiThreadInfo { Size = Marshal.SizeOf<NativeMethods.GuiThreadInfo>() };
        if (!NativeMethods.GetGUIThreadInfo(thread, ref info)) throw new InvalidOperationException("无法确认输入焦点。");
        return info;
    }

    public async Task<InputTarget?> CaptureAsync()
    {
        tracking = null;
        nint window = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(window, out uint process);
        if (window == 0 || process == Environment.ProcessId || hooks.Count != 5) return null;
        long before = Interlocked.Read(ref revision);
        try
        {
            var captured = await Task.Run(() =>
            {
                var info = ThreadInfo(window);
                var element = AutomationElement.FocusedElement;
                if (element == null || element.Current.ProcessId != process || element.Current.IsPassword || !element.Current.IsEnabled) return null;
                if (!element.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)) return null;
                var selection = ((TextPattern)pattern).GetSelection();
                if (selection.Length != 1) return null;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object value) && ((ValuePattern)value).Current.IsReadOnly) return null;
                return new InputTarget
                {
                    Window = window, Process = process, Focus = info.Focus, Element = element,
                    RuntimeId = element.GetRuntimeId(), Selection = selection[0].Clone(),
                    Caret = new NativeCaret(info.CaretRect.Left, info.CaretRect.Top, info.CaretRect.Right, info.CaretRect.Bottom)
                };
            }).WaitAsync(TimeSpan.FromSeconds(2));
            if (before != Interlocked.Read(ref revision) || NativeMethods.GetForegroundWindow() != window) return null;
            tracking = captured;
            return captured;
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or TimeoutException or UnauthorizedAccessException)
        { return null; }
    }

    public void Forget() => tracking = null;

    private static bool Validate(InputTarget target)
    {
        if (target.Changed || !NativeMethods.IsWindow(target.Window) || NativeMethods.GetForegroundWindow() != target.Window) return false;
        NativeMethods.GetWindowThreadProcessId(target.Window, out uint process);
        if (process != target.Process) return false;
        var info = ThreadInfo(target.Window);
        if (info.Focus != target.Focus || new NativeCaret(info.CaretRect.Left, info.CaretRect.Top, info.CaretRect.Right, info.CaretRect.Bottom) != target.Caret) return false;
        var focused = AutomationElement.FocusedElement;
        if (focused == null || focused.Current.IsPassword || !focused.Current.IsEnabled || !focused.GetRuntimeId().SequenceEqual(target.RuntimeId)) return false;
        if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out object pattern)) return false;
        var ranges = ((TextPattern)pattern).GetSelection();
        return ranges.Length == 1 && target.Selection != null &&
            ranges[0].CompareEndpoints(TextPatternRangeEndpoint.Start, target.Selection, TextPatternRangeEndpoint.Start) == 0 &&
            ranges[0].CompareEndpoints(TextPatternRangeEndpoint.End, target.Selection, TextPatternRangeEndpoint.End) == 0;
    }

    public static bool IsSafeText(string text) => text.Length is > 0 and <= 16000 && !text.Any(char.IsControl);

    public async Task<InputOutcome> WriteAsync(InputTarget? target, string text, CancellationToken cancellation)
    {
        if (!IsSafeText(text)) return new(false, "文本为空、过长或包含换行等控制字符，请复制后手动处理；不会模拟 Enter。");
        if (target == null) return new(false, "无法可靠确认输入框。结果已保留，请复制后手动粘贴。");
        try
        {
            for (int i = 0; i < 40; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.All(k => (NativeMethods.GetAsyncKeyState(k) & 0x8000) == 0)) break;
                if (i == 39) return new(false, "请松开修饰键后再输入；结果已保留。");
                await Task.Delay(25, cancellation);
            }
            bool valid = await Task.Run(() => Validate(target), cancellation).WaitAsync(TimeSpan.FromSeconds(2), cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (!valid || target.Changed || NativeMethods.GetForegroundWindow() != target.Window)
                return new(false, "输入目标或光标已变化，已阻止自动写入。请复制结果。");
            var inputs = text.SelectMany(c => new[]
            {
                new NativeMethods.Input { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = NativeMethods.KEYEVENTF_UNICODE } } },
                new NativeMethods.Input { Type = 1, Data = new() { Keyboard = new() { Scan = c, Flags = NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP } } }
            }).ToArray();
            tracking = null;
            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
            return sent == inputs.Length
                ? new(true, "文字输入事件已提交，请检查目标应用；结果仍可复制。")
                : new(false, "系统未完整接受输入，可能是权限限制。请检查目标内容后手动复制，避免重复。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or TimeoutException or UnauthorizedAccessException)
        { return new(false, "无法验证目标控件，已停止写入；请复制结果。"); }
    }

    public void Dispose()
    {
        tracking = null;
        foreach (nint hook in hooks) NativeMethods.UnhookWinEvent(hook);
        hooks.Clear();
    }
}
