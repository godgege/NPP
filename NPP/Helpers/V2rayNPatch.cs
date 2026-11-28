using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace Netch.App.Helpers;

internal static class V2rayNPatch
{
    private const uint DeleteIcon = 2; // NIM_DELETE
    private const uint AvaloniaTrayIconId = 1; // Avalonia assigns the first tray icon ID 1.
    private const string AvaloniaMessageWindowPrefix = "AvaloniaMessageWindow ";

    public static async Task<bool> TryRemoveStartupTrayIconAsync(int processId, Func<bool> isProcessActive,
        CancellationToken cancellationToken)
    {
        var timeout = Stopwatch.StartNew();
        try
        {
            while (timeout.Elapsed < TimeSpan.FromSeconds(5) &&
                !cancellationToken.IsCancellationRequested && isProcessActive())
            {
                if (TryRemoveTrayIcon(processId))
                    return true;
                await Task.Delay(20, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        return false;
    }

    public static bool TryRemoveTrayIcon(int processId)
    {
        var className = new StringBuilder(256);
        var window = EmbeddedWindow.FindProcessWindow(processId, candidate =>
            GetClassName(candidate, className, className.Capacity) > 0 &&
            className.ToString().StartsWith(AvaloniaMessageWindowPrefix, StringComparison.Ordinal));
        if (window == 0)
            return false;

        var data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = window,
            Id = AvaloniaTrayIconId
        };
        // The shell removes only the registration that matches this window and icon ID.
        if (!ShellNotifyIcon(DeleteIcon, ref data))
            return false;
        Log.Debug("Removed the embedded v2rayN tray icon: PID {ProcessId}", processId);
        return true;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string? Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? InfoTitle;
        public uint InfoFlags;
        public Guid IconGuid;
        public nint BalloonIcon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder className, int capacity);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
}
