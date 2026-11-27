using System.Runtime.InteropServices;

namespace Netch.App.Helpers;

internal static class CompanionClientAutomation
{
    // The unmodified Avalonia client exposes its own Exit command through UIA.
    // Invoking that command runs AppExitAsync: save configuration, stop cores,
    // restore the system proxy, and shut down. WM_CLOSE only hides it to tray.
    public static void Exit(nint window, Func<IReadOnlyList<nint>> getDialogs)
    {
        var automation = (IUIAutomation)Activator.CreateInstance(
            Type.GetTypeFromCLSID(new Guid("FF48DBA4-60EF-4201-AA87-54103EEF594E"), throwOnError: true)!)!;
        IUIAutomationElement? root = null;
        IUIAutomationCondition? condition = null;
        IUIAutomationElement? command = null;
        object? pattern = null;
        try
        {
            root = automation.ElementFromHandle(window);
            condition = automation.CreatePropertyCondition(30011, "menuClose"); // AutomationId
            command = root.FindFirst(4, condition); // TreeScope.Descendants
            if (command is null)
                throw new InvalidOperationException("无法找到 v2rayN 的退出命令。");
            pattern = command.GetCurrentPattern(10000); // InvokePattern
            ((IUIAutomationInvokePattern)pattern).Invoke();

            // v2rayN 8.0.1 asks for confirmation even for its own Exit command.
            // Its confirmation has two content buttons, positive first, plus
            // an optional caption Close button preceding them. Restrict
            // this to a new dialog belonging to this companion process, after
            // pre-existing dialogs have been closed by the host.
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            var inspected = new HashSet<nint>();
            while (timeout.Elapsed < TimeSpan.FromSeconds(3))
            {
                foreach (var dialog in getDialogs())
                    if (ConfirmExit(automation, dialog, inspected.Add(dialog)))
                        return;
                Thread.Sleep(50);
            }
            throw new TimeoutException("未收到 v2rayN 的退出确认窗口。");
        }
        finally
        {
            if (pattern is not null) Marshal.ReleaseComObject(pattern);
            if (command is not null) Marshal.ReleaseComObject(command);
            if (condition is not null) Marshal.ReleaseComObject(condition);
            if (root is not null) Marshal.ReleaseComObject(root);
            Marshal.ReleaseComObject(automation);
        }
    }

    private static bool ConfirmExit(IUIAutomation automation, nint dialog, bool logDetails)
    {
        var root = automation.ElementFromHandle(dialog);
        var condition = automation.CreatePropertyCondition(30003, 50000); // ControlType.Button
        IUIAutomationElementArray? buttons = null;
        IUIAutomationElement? positive = null;
        object? pattern = null;
        try
        {
            buttons = root.FindAll(4, condition);
            if (logDetails)
                Serilog.Log.Debug("v2rayN exit confirmation candidate {Handle}: {ButtonCount} buttons", dialog, buttons.Length);
            if (buttons.Length is not (2 or 3))
                return false;
            positive = buttons.GetElement(buttons.Length == 3 ? 1 : 0);
            if (logDetails)
                Serilog.Log.Debug("Invoking v2rayN exit confirmation: {Button}", positive.GetCurrentPropertyValue(30005));
            pattern = positive.GetCurrentPattern(10000);
            ((IUIAutomationInvokePattern)pattern).Invoke();
            return true;
        }
        finally
        {
            if (pattern is not null) Marshal.ReleaseComObject(pattern);
            if (positive is not null) Marshal.ReleaseComObject(positive);
            if (buttons is not null) Marshal.ReleaseComObject(buttons);
            Marshal.ReleaseComObject(condition);
            Marshal.ReleaseComObject(root);
        }
    }

    // These are the leading methods of the documented Windows UI Automation
    // COM interfaces, in native vtable order. Unused slots retain their names;
    // only the three signatures used above need to marshal arguments.
    [ComImport, Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IUIAutomationElement ElementFromHandle(nint window);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void GetControlViewWalker();
        void GetContentViewWalker();
        void GetRawViewWalker();
        void GetRawViewCondition();
        void GetControlViewCondition();
        void GetContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition();
        void CreateFalseCondition();
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }

    [ComImport, Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);
        IUIAutomationElementArray FindAll(int scope, IUIAutomationCondition condition);
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx();
        void GetCachedPropertyValue();
        void GetCachedPropertyValueEx();
        void GetCurrentPatternAs();
        void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetCurrentPattern(int patternId);
    }

    [ComImport, Guid("352FFBA8-0973-437C-A61F-F64CAFD81DF9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("14314595-B4BC-4055-95F2-58F2E42C9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int Length { get; }
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("FB377FBE-8EA6-46D5-9C73-6499642D3059"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationInvokePattern { void Invoke(); }
}
