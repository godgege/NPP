using Netch.Controllers;
using Netch.Interfaces;
using Netch.Models;
using Netch.Models.Modes;
using Netch.Utils;

namespace Netch.Services;

public class ModeService
{
    private readonly NetchAppContext _appContext;
    private readonly IModeListManager _modeListManager;
    private readonly IStatusReporter _statusReporter;

    public ModeService(NetchAppContext appContext, IModeListManager modeListManager, IStatusReporter statusReporter)
    {
        _appContext = appContext;
        _modeListManager = modeListManager;
        _statusReporter = statusReporter;
    }

    public string ModeDirectoryFullName => Path.Combine(_appContext.DataRoot, "mode");

    public string GetRelativePath(string fullName)
    {
        var length = ModeDirectoryFullName.Length;
        if (!ModeDirectoryFullName.EndsWith("\\"))
            length++;

        return fullName.Substring(length);
    }

    public string GetFullPath(string relativeName)
    {
        return Path.Combine(ModeDirectoryFullName, relativeName);
    }

    public void Load()
    {
        PrepareModeDirectory();
        _appContext.Modes.Clear();
        LoadCore(ModeDirectoryFullName);
        Sort();
        _modeListManager.ReloadModes();
    }

    private void PrepareModeDirectory()
    {
        Directory.CreateDirectory(ModeDirectoryFullName);

        var bundledModeDirectory = Path.Combine(_appContext.NetchDir, "mode");
        if (Path.GetFullPath(bundledModeDirectory).Equals(
                Path.GetFullPath(ModeDirectoryFullName),
                StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(bundledModeDirectory))
            return;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(
                         bundledModeDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(bundledModeDirectory, directory);
                Directory.CreateDirectory(Path.Combine(ModeDirectoryFullName, relativePath));
            }

            foreach (var file in Directory.EnumerateFiles(
                         bundledModeDirectory,
                         "*",
                         SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(bundledModeDirectory, file);
                var destination = Path.Combine(ModeDirectoryFullName, relativePath);
                if (!File.Exists(destination))
                    File.Copy(file, destination);
            }
        }
        catch (Exception e)
        {
            // Keep loading any modes already present in the writable directory even
            // when an individual bundled mode cannot be copied.
            Log.Warning(e, "Seed writable mode directory from {ModeDirectory} failed", bundledModeDirectory);
        }
    }

    private void LoadCore(string modeDirectory)
    {
        foreach (var directory in Directory.GetDirectories(modeDirectory))
            LoadCore(directory);

        // skip Directory with a disabled file in
        if (File.Exists(Path.Combine(modeDirectory, Constants.DisableModeDirectoryFileName)))
            return;

        foreach (var file in Directory.GetFiles(modeDirectory))
        {
            try
            {
                _appContext.Modes.Add(ModeHelper.LoadMode(file));
            }
            catch (NotSupportedException)
            {
                // ignored
            }
            catch (Exception e)
            {
                Log.Warning(e, "Load mode \"{FileName}\" failed", file);
            }
        }
    }

    private void SortCollection()
    {
        // TODO better sort need to discuss
        // TODO replace Mode Collection type
        _appContext.Modes.Sort((a, b) => string.Compare(a.i18NRemark, b.i18NRemark, StringComparison.Ordinal));
    }

    public void Add(Mode mode)
    {
        if (mode.FullName == null)
            throw new InvalidOperationException();

        _appContext.Modes.Add(mode);
        Sort();

        mode.WriteFile();
    }

    public void Sort()
    {
        SortCollection();
        _modeListManager.ReloadModes();
    }

    public void Delete(Mode mode)
    {
        if (mode.FullName == null)
            throw new ArgumentException(nameof(mode.FullName));

        _modeListManager.RemoveModeFromList(mode);
        _appContext.Modes.Remove(mode);

        if (File.Exists(mode.FullName))
            File.Delete(mode.FullName);
    }

    public IModeController GetModeControllerByType(ModeType type, out ushort? port, out string portName)
    {
        port = null;
        portName = string.Empty;
        switch (type)
        {
            case ModeType.ProcessMode:
                return new NFController(_appContext, _statusReporter);
            case ModeType.TunMode:
                return new TUNController(_appContext, _statusReporter);
            case ModeType.ShareMode:
                return new PcapController(_appContext, _statusReporter);
            default:
                Log.Error("Unknown Mode Type \"{Type}\"", (int)type);
                throw new MessageException("Unknown Mode Type");
        }
    }
}
