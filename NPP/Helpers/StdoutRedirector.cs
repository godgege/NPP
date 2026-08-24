using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Serilog;

namespace Netch.App.Helpers;

public static class StdoutRedirector
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, SafeHandle hHandle);

    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

    private static SafeFileHandle? _writeHandle;
    private static bool _started;

    public static void StartRedirecting()
    {
        if (_started)
            return;

        try
        {
            if (!CreatePipe(out var hRead, out var hWrite, IntPtr.Zero, 0))
            {
                Log.Warning("CreatePipe failed for native output redirection");
                return;
            }

            if (!SetStdHandle(STD_OUTPUT_HANDLE, hWrite))
            {
                Log.Warning("SetStdHandle failed for native stdout redirection");
                return;
            }

            if (!SetStdHandle(STD_ERROR_HANDLE, hWrite))
            {
                Log.Warning("SetStdHandle failed for native stderr redirection");
                return;
            }

            _writeHandle = hWrite;
            _started = true;

            _ = Task.Run(() =>
            {
                try
                {
                    using var stream = new FileStream(hRead, FileAccess.Read);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            Log.Debug("{NativeOutput}", line);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error reading redirected native output stream");
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to start native output redirection");
        }
    }
}