using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AIfootball.App.Views.Pages;

/// <summary>Hosts the native Unity player window inside a WPF page.</summary>
public sealed class UnityHwndHost : HwndHost
{
    private const int GwlStyle = -16;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const long WsDisabled = 0x08000000L;
    private const int GwlExStyle = -20;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int WmMouseActivate = 0x0021;
    private const int WmSetFocus = 0x0007;
    private IntPtr _hostHandle;
    private IntPtr _unityHandle;
    private readonly TaskCompletionSource<IntPtr> _hostReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string? LastAttachError { get; private set; }
    public nint HostHandle => _hostHandle;

    public async Task<bool> WaitForHostAsync(CancellationToken cancellation)
    {
        if (_hostHandle != IntPtr.Zero) return true;
        try
        {
            await _hostReady.Task.WaitAsync(cancellation);
            return _hostHandle != IntPtr.Zero;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            LastAttachError ??= $"Failed to create WPF Unity host: {ex.Message}";
            return false;
        }
    }

    public async Task<bool> AttachAsync(Process process, CancellationToken cancellation)
    {
        LastAttachError = null;
        IntPtr candidate = IntPtr.Zero;
        var stableChecks = 0;
        for (var attempt = 0; attempt < 150 && !cancellation.IsCancellationRequested; attempt++)
        {
            if (process.HasExited)
            {
                LastAttachError = "Unity exited before creating its window.";
                return false;
            }
            if (_hostHandle == IntPtr.Zero)
            {
                await Task.Delay(100, cancellation);
                continue;
            }
            process.Refresh();
            // With -parentHWND Unity is a child from startup. Fall back to a top-level
            // search only for players that do not support the native parent argument.
            var unityWindow = FindChildWindow(_hostHandle, process.Id);
            if (unityWindow == IntPtr.Zero)
                unityWindow = FindTopLevelWindow(process.Id);
            if (unityWindow != IntPtr.Zero)
            {
                if (candidate != unityWindow)
                {
                    candidate = unityWindow;
                    stableChecks = 0;
                    await Task.Delay(100, cancellation);
                    continue;
                }
                // Unity applies its own startup window settings after creating the HWND.
                // Wait until the HWND has remained unchanged for one second before parenting it.
                if (++stableChecks < 10)
                {
                    await Task.Delay(100, cancellation);
                    continue;
                }

                _unityHandle = candidate;
                var style = GetWindowLongPtr(_unityHandle, GwlStyle).ToInt64();
                style &= ~(WsPopup | WsCaption | WsThickFrame | WsDisabled);
                style |= WsChild;
                SetWindowLongPtr(_unityHandle, GwlStyle, new IntPtr(style));
                var extendedStyle = GetWindowLongPtr(_unityHandle, GwlExStyle).ToInt64();
                SetWindowLongPtr(_unityHandle, GwlExStyle, new IntPtr(extendedStyle & ~WsExNoActivate));
                if (GetParent(_unityHandle) != _hostHandle)
                {
                    SetLastError(0);
                    SetParent(_unityHandle, _hostHandle);
                    var error = Marshal.GetLastWin32Error();
                    if (error != 0 || GetParent(_unityHandle) != _hostHandle)
                    {
                        LastAttachError = error != 0
                            ? new Win32Exception(error).Message
                            : "Unity window parent verification failed.";
                        _unityHandle = IntPtr.Zero;
                        return false;
                    }
                }
                ResizeUnity();
                ShowWindow(_unityHandle, 5);
                SetFocus(_unityHandle);
                return true;
            }
            await Task.Delay(100, cancellation);
        }
        LastAttachError ??= _hostHandle == IntPtr.Zero
            ? "WPF Unity host window was not created."
            : "Unity did not expose a visible top-level window within 15 seconds.";
        return false;
    }

    public void Detach()
    {
        if (_unityHandle == IntPtr.Zero) return;
        // Never let a still-running Unity player flash back onto the desktop.
        if (IsWindow(_unityHandle))
        {
            ShowWindow(_unityHandle, 0);
            SetParent(_unityHandle, IntPtr.Zero);
        }
        _unityHandle = IntPtr.Zero;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hostHandle = CreateWindowEx(0, "STATIC", string.Empty, WsChild | 0x10000000L,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hostHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            LastAttachError = $"Failed to create WPF Unity host: {new Win32Exception(error).Message} ({error}).";
            _hostReady.TrySetException(new Win32Exception(error));
        }
        else
        {
            _hostReady.TrySetResult(_hostHandle);
        }
        return new HandleRef(this, _hostHandle);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_unityHandle != IntPtr.Zero && IsWindow(_unityHandle))
            ShowWindow(_unityHandle, 0);
        _unityHandle = IntPtr.Zero;
        if (hwnd.Handle != IntPtr.Zero) DestroyWindow(hwnd.Handle);
        _hostHandle = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeUnity();
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((msg == WmMouseActivate || msg == WmSetFocus) && _unityHandle != IntPtr.Zero)
        {
            SetFocus(_unityHandle);
            if (msg == WmMouseActivate)
            {
                handled = true;
                return new IntPtr(1); // MA_ACTIVATE
            }
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void ResizeUnity()
    {
        if (_unityHandle == IntPtr.Zero || _hostHandle == IntPtr.Zero) return;
        // WPF reports DIPs here, while SetWindowPos expects physical pixels.
        // Reading the host client rect keeps Unity edge-to-edge at any Windows DPI scale.
        if (!GetClientRect(_hostHandle, out var clientArea)) return;
        var hostWidth = Math.Max(1, clientArea.Right - clientArea.Left);
        var hostHeight = Math.Max(1, clientArea.Bottom - clientArea.Top);
        SetWindowPos(_unityHandle, IntPtr.Zero, 0, 0, hostWidth, hostHeight,
            SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
    }

    private static IntPtr FindTopLevelWindow(int processId)
    {
        var result = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId == processId)
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static IntPtr FindChildWindow(IntPtr parentHandle, int processId)
    {
        var result = IntPtr.Zero;
        EnumChildWindows(parentHandle, (window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId == processId)
            {
                result = window;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(long exStyle, string className, string windowName, long style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
