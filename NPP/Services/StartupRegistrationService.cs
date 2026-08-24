using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace Netch.App.Services;

public sealed class StartupRegistrationService
{
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string CurrentTaskNamePrefix = "NPP Startup";

    private static readonly string[] LegacyRegistryValueNames = ["NPP", "Netch"];
    private static readonly string[] LegacyTaskNames = ["NPP Startup", "Netch Startup"];

    public const string StartupArgument = "--startup";

    private readonly NetchAppContext _appContext;
    private readonly string _currentUserAccount;
    private readonly string _currentUserSid;
    private readonly string _taskName;

    public StartupRegistrationService(NetchAppContext appContext)
    {
        _appContext = appContext;

        using var identity = WindowsIdentity.GetCurrent();
        _currentUserSid = identity.User?.Value
                          ?? throw new InvalidOperationException("无法获取当前 Windows 用户 SID。");
        _currentUserAccount = string.IsNullOrWhiteSpace(identity.Name)
            ? _currentUserSid
            : identity.Name;
        _taskName = $"{CurrentTaskNamePrefix} - {_currentUserSid}";
    }

    public bool IsRegistered
    {
        get
        {
            using var task = TaskService.Instance.GetTask(_taskName);
            if (task is null || !task.Enabled)
                return false;

            var definition = task.Definition;
            var action = definition.Actions.OfType<ExecAction>().SingleOrDefault();
            var trigger = definition.Triggers.OfType<LogonTrigger>().SingleOrDefault();

            return action is not null &&
                   trigger is not null &&
                   definition.Actions.Count == 1 &&
                   definition.Triggers.Count == 1 &&
                   definition.Principal.RunLevel == TaskRunLevel.Highest &&
                   definition.Principal.LogonType == TaskLogonType.InteractiveToken &&
                   IsCurrentUser(definition.Principal.UserId) &&
                   IsCurrentUser(trigger.UserId) &&
                   PathsEqual(action.Path, _appContext.NetchExecutable) &&
                   string.Equals(action.Arguments?.Trim(), StartupArgument, StringComparison.Ordinal) &&
                   PathsEqual(action.WorkingDirectory, _appContext.NetchDir);
        }
    }

    public void Apply(bool enabled)
    {
        if (enabled)
        {
            // Keep the legacy registration intact until the replacement has
            // been created successfully, so a transient Task Scheduler error
            // cannot silently remove the user's only startup path.
            RegisterOrUpdateTask();
            CleanupLegacyRunEntries();
            CleanupLegacyTasks();
            return;
        }

        DeleteTask(_taskName);
        CleanupLegacyRunEntries();
        CleanupLegacyTasks();
    }

    public void Reconcile(bool enabled)
    {
        if (!enabled)
        {
            DeleteTask(_taskName);
            CleanupLegacyRunEntries();
            CleanupLegacyTasks();
            return;
        }

        if (!IsRegistered)
            RegisterOrUpdateTask();

        CleanupLegacyRunEntries();
        CleanupLegacyTasks();
    }

    private void RegisterOrUpdateTask()
    {
        using var definition = TaskService.Instance.NewTask();
        definition.RegistrationInfo.Author = "NPP";
        definition.RegistrationInfo.Description = "以最高权限在当前用户登录时启动 NPP。";

        definition.Principal.UserId = _currentUserAccount;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = TaskRunLevel.Highest;

        definition.Triggers.Add(new LogonTrigger
        {
            UserId = _currentUserAccount
        });
        definition.Actions.Add(new ExecAction(
            _appContext.NetchExecutable,
            StartupArgument,
            _appContext.NetchDir));

        definition.Settings.AllowDemandStart = true;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
        definition.Settings.IdleSettings.RestartOnIdle = false;
        definition.Settings.IdleSettings.StopOnIdleEnd = false;
        definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
        definition.Settings.RunOnlyIfIdle = false;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.Compatibility = TaskCompatibility.V2_1;

        TaskService.Instance.RootFolder.RegisterTaskDefinition(
            _taskName,
            definition,
            TaskCreation.CreateOrUpdate,
            _currentUserAccount,
            password: null,
            TaskLogonType.InteractiveToken);
    }

    private static void CleanupLegacyRunEntries()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        if (key is null)
            return;

        foreach (var valueName in LegacyRegistryValueNames)
            key.DeleteValue(valueName, throwOnMissingValue: false);
    }

    private static void CleanupLegacyTasks()
    {
        foreach (var taskName in LegacyTaskNames)
            DeleteTask(taskName);
    }

    private static void DeleteTask(string taskName)
    {
        TaskService.Instance.RootFolder.DeleteTask(taskName, exceptionOnNotExists: false);
    }

    private bool IsCurrentUser(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return false;

        if (string.Equals(userId, _currentUserSid, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(userId, _currentUserAccount, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            var sid = new NTAccount(userId)
                .Translate(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return string.Equals(sid?.Value, _currentUserSid, StringComparison.OrdinalIgnoreCase);
        }
        catch (IdentityNotMappedException)
        {
            return false;
        }
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
            return false;

        try
        {
            var leftPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(left.Trim().Trim('"')));
            var rightPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(right.Trim().Trim('"')));
            return leftPath.Equals(rightPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
