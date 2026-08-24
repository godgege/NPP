using WindowsFirewallHelper;
using WindowsFirewallHelper.FirewallRules;

namespace Netch.Utils;

public static class Firewall
{
    private const string Netch = "Netch";

    /// <summary>
    ///     Netch 自带程序添加防火墙
    /// </summary>
    public static void AddNetchFwRules(string netchDir)
    {
        if (!FirewallWAS.IsLocallySupported)
        {
            Log.Warning("Windows Firewall Locally Unsupported");
            return;
        }

        try
        {
            var rule = FirewallManager.Instance.Rules.FirstOrDefault(r => r.Name == Netch);
            if (rule != null)
            {
                if (rule.ApplicationName.StartsWith(netchDir))
                    return;

                RemoveNetchFwRules(netchDir);
            }

            foreach (var path in Directory.GetFiles(netchDir, "*.exe", SearchOption.AllDirectories))
                AddFwRule(Netch, path);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Create Netch Firewall rules error");
        }
    }

    /// <summary>
    ///     清除防火墙规则 (Netch 自带程序)
    /// </summary>
    public static void RemoveNetchFwRules(string netchDir)
    {
        if (!FirewallWAS.IsLocallySupported)
            return;

        try
        {
            foreach (var rule in FirewallManager.Instance.Rules.Where(r
                         => r.ApplicationName?.StartsWith(netchDir, StringComparison.OrdinalIgnoreCase) ?? r.Name == Netch))
                FirewallManager.Instance.Rules.Remove(rule);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Remove Netch Firewall rules error");
        }
    }

    #region 封装

    private static void AddFwRule(string ruleName, string exeFullPath)
    {
        var rule = new FirewallWASRule(ruleName,
            exeFullPath,
            FirewallAction.Allow,
            FirewallDirection.Inbound,
            FirewallProfiles.Private | FirewallProfiles.Public | FirewallProfiles.Domain);

        FirewallManager.Instance.Rules.Add(rule);
    }

    #endregion
}