using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Netch.App.Helpers;

/// <summary>Hosts a companion process's native window in a clipped child HWND.</summary>
internal sealed class EmbeddedWindow : IDisposable
{
    private const int StyleIndex = -16;
    private const int ExtendedStyleIndex = -20;
    private const long Child = 0x40000000;
    private const long Popup = 0x80000000;
    private const long Frame = 0x00CF0000;
    private const long AppWindow = 0x00040000;
    private const long ToolWindow = 0x00000080;
    private nint _host;
    private nint _child;
    private int _processId;
    private readonly HashSet<nint> _hiddenDialogs = [];
    private (int X, int Y, int Width, int Height) _bounds;

    public bool IsAttached => _child != 0 && IsWindow(_child);

    public void Create(nint owner)
    {
        if (_host != 0)
            return;
        _host = CreateWindowEx(0x00010000, "Static", "NPP v2rayN host",
            Child | 0x06000000, 0, 0, 0, 0, owner, 0, 0, 0);
        if (_host == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 v2rayN 窗口容器。");
        if (GetClientRect(owner, out var client))
        {
            var tabHeight = (int)Math.Round(48 * GetDpiForWindow(owner) / 96d);
            SetBounds(0, tabHeight, client.Right, client.Bottom - tabHeight);
        }
    }

    public void Attach(nint window)
    {
        ShowWindow(window, 0);
        var style = GetWindowLongPtr(window, StyleIndex).ToInt64();
        var extendedStyle = GetWindowLongPtr(window, ExtendedStyleIndex).ToInt64();
        SetWindowLongPtr(window, StyleIndex, (nint)((style & ~(Popup | Frame)) | Child));
        SetWindowLongPtr(window, ExtendedStyleIndex, (nint)((extendedStyle & ~AppWindow) | ToolWindow));
        Marshal.SetLastPInvokeError(0);
        var previousParent = SetParent(window, _host);
        var error = Marshal.GetLastWin32Error();
        if (previousParent == 0 && error != 0)
        {
            SetWindowLongPtr(window, StyleIndex, (nint)style);
            SetWindowLongPtr(window, ExtendedStyleIndex, (nint)extendedStyle);
            throw new Win32Exception(error, "无法将 v2rayN 窗口嵌入 NPP。");
        }
        _child = window;
        GetWindowThreadProcessId(window, out _processId);
        ResizeChild();
    }

    public void SetBounds(int x, int y, int width, int height)
    {
        var bounds = (x, y, Math.Max(1, width), Math.Max(1, height));
        if (_host == 0 || bounds == _bounds)
            return;
        _bounds = bounds;
        SetWindowPos(_host, 0, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, 0x0010);
        ResizeChild();
    }

    private void ResizeChild()
    {
        if (IsAttached)
            SetWindowPos(_child, 0, 0, 0, Math.Max(1, _bounds.Width), Math.Max(1, _bounds.Height), 0x0034);
    }

    public void SetVisible(bool visible)
    {
        if (_host == 0)
            return;
        if (visible && IsAttached)
        {
            if (IsIconic(_child))
                ShowWindow(_child, 9);
            ShowWindow(_child, 4);
            ResizeChild();
        }
        ShowWindow(_host, visible ? 4 : 0);
        if (!visible)
        {
            EnumerateDialogs(window =>
            {
                if (IsWindowVisible(window))
                {
                    _hiddenDialogs.Add(window);
                    ShowWindow(window, 0);
                }
            });
        }
        else
        {
            foreach (var dialog in _hiddenDialogs)
                if (IsWindow(dialog))
                    ShowWindow(dialog, 4);
            _hiddenDialogs.Clear();
        }
    }

    public static nint FindMainWindow(int processId) => FindProcessWindow(processId, window =>
        GetWindow(window, 4) == 0 && GetWindowTextLength(window) > 0 && GetWindowRect(window, out var rect) &&
        rect.Right - rect.Left >= 400 && rect.Bottom - rect.Top >= 250);

    internal static nint FindProcessWindow(int processId, Func<nint, bool> matches)
    {
        nint result = 0;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerId);
            if (ownerId == processId && matches(window))
            {
                result = window;
                return false;
            }
            return true;
        }, 0);
        return result;
    }

    public void RequestShutdown()
    {
        if (!IsAttached)
            return;
        EnumerateDialogs(window => SendMessageTimeout(window, 0x0010, 0, 0, 0x0002, 1000, out _));
        var existingDialogs = new HashSet<nint>();
        EnumerateDialogs(dialog => existingDialogs.Add(dialog));
        CompanionClientAutomation.Exit(_child, () =>
        {
            var dialogs = new List<nint>();
            EnumerateDialogs(dialog =>
            {
                if (!existingDialogs.Contains(dialog))
                    dialogs.Add(dialog);
            });
            return dialogs;
        });
    }

    private void EnumerateDialogs(Action<nint> action)
    {
        if (_processId == 0)
            return;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == _processId && window != _child && GetWindowTextLength(window) > 0)
                action(window);
            return true;
        }, 0);
    }

    public void ClearChild()
    {
        _child = 0;
        _processId = 0;
        _hiddenDialogs.Clear();
    }

    public void Dispose()
    {
        ClearChild();
        if (_host != 0)
            DestroyWindow(_host);
        _host = 0;
        _bounds = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(int extendedStyle, string className, string title,
        long style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetParent(nint child, nint parent);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int processId);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
}

/// <summary>Closes the companion and all its cores if NPP exits unexpectedly.</summary>
internal sealed class CompanionProcessJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    public CompanionProcessJob()
    {
        _handle = CreateJobObject(0, null);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(_handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        {
            var error = Marshal.GetLastWin32Error();
            _handle.Dispose();
            throw new Win32Exception(error);
        }
    }

    public void Add(System.Diagnostics.Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法管理 v2rayN 子进程。");
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}
