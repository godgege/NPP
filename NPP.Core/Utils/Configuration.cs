using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.Threading;
using Netch.JsonConverter;
using Netch.Models;

namespace Netch.Utils;

public class Configuration
{
    private readonly NetchAppContext _appContext;

    private readonly AsyncReaderWriterLock _lock = new(null);

    private readonly JsonSerializerOptions _jsonSerializerOptions;

    /// <summary>
    ///     数据目录
    /// </summary>
    public string DataDirectoryFullName => Path.Combine(_appContext.DataRoot, "data");

    public string FileFullName => Path.Combine(DataDirectoryFullName, FileName);

    private string BackupFileFullName => Path.Combine(DataDirectoryFullName, BackupFileName);

    private const string FileName = "settings.json";

    private const string BackupFileName = "settings.json.bak";

    public Configuration(NetchAppContext appContext)
    {
        _appContext = appContext;
        _jsonSerializerOptions = NetchAppContext.NewCustomJsonSerializerOptions();
        _jsonSerializerOptions.Converters.Add(new ServerConverterWithTypeDiscriminator());
        _jsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task LoadAsync()
    {
        try
        {
            TryMigrateLegacyConfiguration();

            if (!File.Exists(FileFullName))
            {
                // Startup must not continue with an in-memory default configuration
                // when the initial settings file could not actually be persisted.
                await SaveOrThrowAsync();
                return;
            }

            await using var _ = await _lock.ReadLockAsync();

            if (await LoadCoreAsync(FileFullName))
                return;

            Log.Information("Load backup configuration \"{FileName}\"", BackupFileFullName);
            await LoadCoreAsync(BackupFileFullName);
        }
        catch (Exception e)
        {
            Log.Error(e, "Load configuration failed");
            Environment.Exit(-1);
        }
    }

    private async ValueTask<bool> LoadCoreAsync(string filename)
    {
        try
        {
            Setting settings;

            await using (var fs = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true))
            {
                settings = (await JsonSerializer.DeserializeAsync<Setting>(fs, _jsonSerializerOptions))!;
            }

            CheckSetting(settings);
            _appContext.Settings = settings;
            return true;
        }
        catch (Exception e)
        {
            Log.Error(e, "Load configuration file \"{FileName}\" error ", filename);
            return false;
        }
    }

    private static void CheckSetting(Setting settings)
    {
        settings.Profiles.RemoveAll(p => p.ServerRemark == string.Empty || p.ModeRemark == string.Empty);

        if (settings.Profiles.Any(p => settings.Profiles.Any(p1 => p1 != p && p1.Index == p.Index)))
            for (var i = 0; i < settings.Profiles.Count; i++)
                settings.Profiles[i].Index = i;

        settings.AioDNS.ChinaDNS = DnsUtils.AppendPort(settings.AioDNS.ChinaDNS);
        settings.AioDNS.OtherDNS = DnsUtils.AppendPort(settings.AioDNS.OtherDNS);
    }

    /// <summary>
    ///     保存配置
    /// </summary>
    public async Task SaveAsync()
    {
        try
        {
            await SaveOrThrowAsync();
        }
        catch (Exception e)
        {
            Log.Error(e, "Save Configuration error");
        }
    }

    private void TryMigrateLegacyConfiguration()
    {
        var legacyDirectory = Path.Combine(_appContext.NetchDir, "data");
        if (Path.GetFullPath(legacyDirectory).Equals(
                Path.GetFullPath(DataDirectoryFullName),
                StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            CopyLegacyFileIfMissing(legacyDirectory, FileName);
            CopyLegacyFileIfMissing(legacyDirectory, BackupFileName);
        }
        catch (Exception e)
        {
            // A failed best-effort migration must not prevent the writable data root
            // from being initialized with a fresh configuration.
            Log.Warning(e, "Migrate legacy configuration from {LegacyDirectory} failed", legacyDirectory);
        }
    }

    private void CopyLegacyFileIfMissing(string legacyDirectory, string fileName)
    {
        var source = Path.Combine(legacyDirectory, fileName);
        var destination = Path.Combine(DataDirectoryFullName, fileName);
        if (!File.Exists(source) || File.Exists(destination))
            return;

        Directory.CreateDirectory(DataDirectoryFullName);
        File.Copy(source, destination);
        Log.Information("Migrated legacy configuration file {FileName}", fileName);
    }

    /// <summary>
    ///     保存配置，并将写入错误交给需要反馈结果的调用方处理。
    /// </summary>
    public async Task SaveOrThrowAsync()
    {
        if (_lock.IsWriteLockHeld)
            return;

        await using var _ = await _lock.WriteLockAsync();
        Log.Verbose("Save Configuration");

        if (!Directory.Exists(DataDirectoryFullName))
            Directory.CreateDirectory(DataDirectoryFullName);

        var tempFile = Path.Combine(DataDirectoryFullName, FileName + ".tmp");
        await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(fileStream, _appContext.Settings, _jsonSerializerOptions);
        }

        await EnsureConfigFileExistsAsync();

        File.Replace(tempFile, FileFullName, BackupFileFullName);
    }

    private async ValueTask EnsureConfigFileExistsAsync()
    {
        if (!File.Exists(FileFullName))
        {
            await using var fs = new FileStream(FileFullName, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 4096, true);
        }
    }
}
