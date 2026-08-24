using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;
using Netch.App.Models;
using Serilog;
using Windows.Management.Deployment;

namespace Netch.App.Services;

public static class AppDiscoveryService
{
    public static Task<List<InstalledApp>> DiscoverInstalledAppsAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<List<InstalledApp>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(DiscoverInstalledApps());
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception e)
            {
                completion.TrySetException(e);
            }
        })
        {
            IsBackground = true,
            Name = "NPP application discovery"
        };

        // Shell shortcut resolution uses apartment-threaded COM. Keeping the
        // complete discovery pass on a dedicated STA thread avoids blocking the
        // WinUI dispatcher while preserving that requirement.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static List<InstalledApp> DiscoverInstalledApps()
    {
        var totalStopwatch = Stopwatch.StartNew();
        var apps = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        var sourceStats = new[]
        {
            ScanRegistry(apps, Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "Registry HKCU"),
            ScanRegistry(apps, Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "Registry HKLM64"),
            ScanRegistry(apps, Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", "Registry HKLM32"),
            ScanStartMenu(apps),
            ScanPackagedApps(apps)
        };

        totalStopwatch.Stop();
        foreach (var stats in sourceStats)
        {
            Log.Debug(
                "[AppDiscovery] {Source}: scanned={Scanned} resolved={Resolved} added={Added} " +
                "duplicate={Duplicate} filtered={Filtered} errors={Errors} elapsed={ElapsedMs}ms",
                stats.Source, stats.Scanned, stats.Resolved, stats.Added,
                stats.Duplicate, stats.Filtered, stats.Errors, stats.ElapsedMs);
        }

        Log.Debug("[AppDiscovery] Completed: unique={Unique} elapsed={ElapsedMs}ms",
            apps.Count, totalStopwatch.ElapsedMilliseconds);

        return apps.Values
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DiscoveryStats ScanRegistry(
        Dictionary<string, InstalledApp> apps,
        RegistryKey rootKey,
        string keyPath,
        string source)
    {
        return MeasureSource(source, stats =>
        {
            using var key = rootKey.OpenSubKey(keyPath);
            if (key == null) return;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                stats.Scanned++;
                try
                {
                    using var subKey = key.OpenSubKey(subKeyName);
                    if (subKey == null)
                    {
                        stats.Filtered++;
                        continue;
                    }

                    var displayName = subKey.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        stats.Filtered++;
                        continue;
                    }

                    var systemComponent = subKey.GetValue("SystemComponent");
                    if (systemComponent is int sc && sc == 1)
                    {
                        stats.Filtered++;
                        continue;
                    }

                    var installLocation = subKey.GetValue("InstallLocation") as string;
                    var displayIcon = subKey.GetValue("DisplayIcon") as string;

                    var installPath = ResolveInstallPath(installLocation, displayIcon);
                    if (installPath == null)
                    {
                        stats.Filtered++;
                        continue;
                    }

                    stats.Resolved++;
                    if (apps.ContainsKey(installPath))
                    {
                        stats.Duplicate++;
                        continue;
                    }

                    apps[installPath] = new InstalledApp
                    {
                        Name = displayName,
                        InstallPath = installPath,
                        ExePath = ResolveExePath(displayIcon)
                    };
                    stats.Added++;
                }
                catch
                {
                    stats.Errors++;
                }
            }
        });
    }

    private static DiscoveryStats ScanStartMenu(Dictionary<string, InstalledApp> apps)
    {
        return MeasureSource("StartMenu", stats =>
        {
            var folders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
            };

            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;

                foreach (var lnk in EnumerateFilesSafe(folder, "*.lnk", () => stats.Errors++))
                {
                    stats.Scanned++;
                    try
                    {
                        var targetPath = ResolveShortcut(lnk);
                        if (targetPath == null ||
                            !targetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            !File.Exists(targetPath))
                        {
                            stats.Filtered++;
                            continue;
                        }

                        var installPath = Path.GetDirectoryName(targetPath);
                        if (installPath == null || IsSystemPath(installPath))
                        {
                            stats.Filtered++;
                            continue;
                        }

                        stats.Resolved++;
                        if (apps.ContainsKey(installPath))
                        {
                            stats.Duplicate++;
                            continue;
                        }

                        apps[installPath] = new InstalledApp
                        {
                            Name = Path.GetFileNameWithoutExtension(lnk),
                            InstallPath = installPath,
                            ExePath = targetPath
                        };
                        stats.Added++;
                    }
                    catch
                    {
                        stats.Errors++;
                    }
                }
            }
        });
    }

    private static DiscoveryStats ScanPackagedApps(Dictionary<string, InstalledApp> apps)
    {
        return MeasureSource("Appx", stats =>
        {
            var packageManager = new PackageManager();
            foreach (var package in packageManager.FindPackagesForUser(string.Empty))
            {
                stats.Scanned++;
                try
                {
                    if (package.IsFramework || package.IsResourcePackage || !package.Status.VerifyIsOK())
                    {
                        stats.Filtered++;
                        continue;
                    }

                    var packagePath = package.InstalledLocation?.Path;
                    if (string.IsNullOrWhiteSpace(packagePath) || IsSystemPath(packagePath))
                    {
                        stats.Filtered++;
                        continue;
                    }

                    var manifestPath = Path.Combine(packagePath, "AppxManifest.xml");
                    if (!File.Exists(manifestPath))
                    {
                        stats.Filtered++;
                        continue;
                    }

                    var manifest = XDocument.Load(manifestPath);
                    var fallbackName = GetPackageDisplayName(manifest, package.DisplayName);
                    var resolvedPackage = false;

                    foreach (var application in manifest.Descendants().Where(e => e.Name.LocalName == "Application"))
                    {
                        var executable = application.Attribute("Executable")?.Value;
                        var exePath = ResolvePackageExecutable(packagePath, executable);
                        if (exePath == null)
                            continue;

                        var installPath = Path.GetDirectoryName(exePath);
                        if (installPath == null)
                            continue;

                        resolvedPackage = true;
                        if (apps.ContainsKey(installPath))
                        {
                            stats.Duplicate++;
                            continue;
                        }

                        apps[installPath] = new InstalledApp
                        {
                            Name = GetApplicationDisplayName(application, fallbackName),
                            InstallPath = installPath,
                            ExePath = exePath
                        };
                        stats.Added++;
                    }

                    if (resolvedPackage)
                        stats.Resolved++;
                    else
                        stats.Filtered++;
                }
                catch
                {
                    stats.Errors++;
                }
            }
        });
    }

    private static DiscoveryStats MeasureSource(string source, Action<DiscoveryStats> scan)
    {
        var stats = new DiscoveryStats(source);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            scan(stats);
        }
        catch (Exception e)
        {
            stats.Errors++;
            Log.Debug(e, "[AppDiscovery] {Source} scan failed", source);
        }

        stopwatch.Stop();
        stats.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return stats;
    }

    private static string? ResolveInstallPath(string? installLocation, string? displayIcon)
    {
        if (!string.IsNullOrWhiteSpace(installLocation) && Directory.Exists(installLocation))
            return installLocation.TrimEnd('\\');

        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var iconPath = displayIcon.Split(',')[0].Trim('"');
            if (File.Exists(iconPath))
            {
                var dir = Path.GetDirectoryName(iconPath);
                if (dir != null && !IsSystemPath(dir))
                    return dir;
            }
        }

        return null;
    }

    private static string? ResolveExePath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;

        var path = displayIcon.Split(',')[0].Trim('"');
        return File.Exists(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string? ResolvePackageExecutable(string packagePath, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return null;

        try
        {
            var packageRoot = Path.GetFullPath(packagePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relativePath = executable.Trim().Trim('"')
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var exePath = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
            var packageRootPrefix = packageRoot + Path.DirectorySeparatorChar;

            if (!exePath.StartsWith(packageRootPrefix, StringComparison.OrdinalIgnoreCase))
                return null;

            return exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(exePath)
                ? exePath
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetPackageDisplayName(XDocument manifest, string fallbackName)
    {
        var displayName = manifest.Root?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "Properties")?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?
            .Value;

        return GetLiteralDisplayName(displayName) ?? fallbackName;
    }

    private static string GetApplicationDisplayName(XElement application, string fallbackName)
    {
        var displayName = application.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "VisualElements")?
            .Attribute("DisplayName")?
            .Value;

        return GetLiteralDisplayName(displayName) ?? fallbackName;
    }

    private static string? GetLiteralDisplayName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) ||
            displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return null;

        return displayName.Trim();
    }

    private static bool IsSystemPath(string path)
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(winDir, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern, Action onError)
    {
        Queue<string> dirs = new();
        dirs.Enqueue(root);

        while (dirs.Count > 0)
        {
            var dir = dirs.Dequeue();
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, pattern);
            }
            catch (UnauthorizedAccessException) { onError(); continue; }
            catch (DirectoryNotFoundException) { onError(); continue; }

            foreach (var f in files)
                yield return f;

            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(dir))
                    dirs.Enqueue(subDir);
            }
            catch (UnauthorizedAccessException) { onError(); }
            catch (DirectoryNotFoundException) { onError(); }
        }
    }

    private static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var shellLinkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (shellLinkType == null) return null;

            var shellLink = Activator.CreateInstance(shellLinkType);
            if (shellLink == null) return null;

            var persistFile = (IPersistFile)shellLink;
            persistFile.Load(lnkPath, 0);

            var link = (IShellLink)shellLink;
            var sb = new char[260];
            unsafe
            {
                fixed (char* p = sb)
                {
                    link.GetPath(p, 260, IntPtr.Zero, 0);
                }
            }

            var target = new string(sb).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, int dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName);
        void GetCurFile(out IntPtr ppszFileName);
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IShellLink
    {
        void GetPath(char* pszFile, int cch, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription(char* pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory(char* pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments(char* pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation(char* pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    private sealed class DiscoveryStats
    {
        public string Source { get; }
        public int Scanned { get; set; }
        public int Resolved { get; set; }
        public int Added { get; set; }
        public int Duplicate { get; set; }
        public int Filtered { get; set; }
        public int Errors { get; set; }
        public long ElapsedMs { get; set; }

        public DiscoveryStats(string source)
        {
            Source = source;
        }
    }
}
