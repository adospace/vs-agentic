using System.Reflection;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security;

namespace VsAgentic.VSExtension.Notifications;

/// <summary>
/// Shows, replaces and removes Windows toast notifications. A toast shows on
/// every virtual desktop and stays in the notification center, which is what a
/// Visual Studio on another desktop needs to reach the user.
/// </summary>
/// <remarks>
/// The extension targets .NET Framework, which projects WinRT types at run time
/// but has no compile-time reference to them without an extra package. The few
/// calls needed here therefore go through reflection. Every failure is reported
/// as <c>false</c> or swallowed: a missing toast must never break a session.
/// </remarks>
internal sealed class WindowsToast
{
    private const string NotificationsAssembly = "Windows.UI.Notifications, ContentType=WindowsRuntime";
    private const string XmlAssembly = "Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime";

    private readonly string _appId;
    private readonly Action<string, Exception?> _log;

    // Looked up by name rather than from GetType() on the objects WinRT hands
    // back, which may be bare COM wrappers.
    private readonly Type? _managerType;
    private readonly Type? _toastType;
    private readonly Type? _notifierType;
    private readonly Type? _historyType;
    private readonly Type? _xmlType;
    private object? _notifier;

    // Toasts that are shown, by tag. Holding the object keeps its Activated
    // handler alive for as long as the toast can still be clicked.
    private readonly Dictionary<string, object> _shown = new();

    /// <param name="appId">
    /// AppUserModelID the toast is shown under. Windows shows the toast only
    /// for an ID that has a Start menu shortcut, and names and draws the toast
    /// after that shortcut.
    /// </param>
    /// <param name="group">
    /// Group of every toast this instance shows. All Visual Studio instances
    /// share one AppUserModelID, so the group keeps one instance from removing
    /// another one's toasts.
    /// </param>
    public WindowsToast(string appId, string group, Action<string, Exception?> log)
    {
        _appId = appId;
        Group = group;
        _log = log;

        _managerType = Type.GetType($"Windows.UI.Notifications.ToastNotificationManager, {NotificationsAssembly}", false);
        _toastType = Type.GetType($"Windows.UI.Notifications.ToastNotification, {NotificationsAssembly}", false);
        _notifierType = Type.GetType($"Windows.UI.Notifications.ToastNotifier, {NotificationsAssembly}", false);
        _historyType = Type.GetType($"Windows.UI.Notifications.ToastNotificationHistory, {NotificationsAssembly}", false);
        _xmlType = Type.GetType($"Windows.Data.Xml.Dom.XmlDocument, {XmlAssembly}", false);
    }

    public string Group { get; }

    public bool IsAvailable =>
        _managerType is not null && _toastType is not null && _notifierType is not null
        && _historyType is not null && _xmlType is not null;

    /// <summary>
    /// Shows a toast, replacing any earlier one with the same tag.
    /// <paramref name="activated"/> runs on a thread-pool thread when the user
    /// clicks the toast.
    /// </summary>
    public bool Show(string tag, string title, string text, string attribution, Action activated)
    {
        if (!IsAvailable) return false;

        try
        {
            var xml = Activator.CreateInstance(_xmlType!);
            _xmlType!.GetMethod("LoadXml", new[] { typeof(string) })!
                .Invoke(xml, new object[] { BuildXml(title, text, attribution) });

            var toast = Activator.CreateInstance(_toastType!, xml)!;
            _toastType!.GetProperty("Tag")!.SetValue(toast, tag);
            _toastType.GetProperty("Group")!.SetValue(toast, Group);

            AddActivatedHandler(toast, new ActivatedHandler(activated, _log));

            _notifierType!.GetMethod("Show")!.Invoke(GetNotifier(), new[] { toast });

            _shown[tag] = toast;
            return true;
        }
        catch (Exception ex)
        {
            _log("Could not show the Windows notification", Unwrap(ex));
            return false;
        }
    }

    /// <summary>
    /// Takes the toast with this tag off the screen and out of the
    /// notification center. Safe to call for a tag that has no toast.
    /// </summary>
    public void Remove(string tag)
    {
        if (!_shown.Remove(tag) || !IsAvailable) return;

        try
        {
            _historyType!.GetMethod("Remove", new[] { typeof(string), typeof(string), typeof(string) })!
                .Invoke(GetHistory(), new object[] { tag, Group, _appId });
        }
        catch (Exception ex)
        {
            _log("Could not remove the Windows notification", Unwrap(ex));
        }
    }

    /// <summary>Removes every toast this instance has shown.</summary>
    public void RemoveAll()
    {
        if (_shown.Count == 0 || !IsAvailable) return;
        _shown.Clear();

        try
        {
            _historyType!.GetMethod("RemoveGroup", new[] { typeof(string), typeof(string) })!
                .Invoke(GetHistory(), new object[] { Group, _appId });
        }
        catch (Exception ex)
        {
            _log("Could not remove the Windows notifications", Unwrap(ex));
        }
    }

    private object GetNotifier() =>
        _notifier ??= _managerType!.GetMethod("CreateToastNotifier", new[] { typeof(string) })!
            .Invoke(null, new object[] { _appId })!;

    /// <summary>
    /// Subscribes to the toast's Activated event. EventInfo.AddEventHandler
    /// refuses WinRT events, whose add method returns a registration token, so
    /// the subscription goes through <see cref="WindowsRuntimeMarshal"/> as the
    /// compiler would emit it.
    /// </summary>
    private void AddActivatedHandler(object toast, ActivatedHandler handler)
    {
        var activatedEvent = _toastType!.GetEvent("Activated")!;
        var handlerType = activatedEvent.EventHandlerType!;
        var tokenType = typeof(EventRegistrationToken);

        var callback = Delegate.CreateDelegate(
            handlerType, handler, typeof(ActivatedHandler).GetMethod(nameof(ActivatedHandler.Invoke))!);
        var add = Delegate.CreateDelegate(
            typeof(Func<,>).MakeGenericType(handlerType, tokenType), toast, activatedEvent.GetAddMethod()!);
        var remove = Delegate.CreateDelegate(
            typeof(Action<>).MakeGenericType(tokenType), toast, activatedEvent.GetRemoveMethod()!);

        typeof(WindowsRuntimeMarshal).GetMethod(nameof(WindowsRuntimeMarshal.AddEventHandler))!
            .MakeGenericMethod(handlerType)
            .Invoke(null, new object[] { add, remove, callback });
    }

    private object GetHistory() => _managerType!.GetProperty("History")!.GetValue(null)!;

    /// <summary>Tags of this instance's toasts still in the notification center. For tests.</summary>
    internal IReadOnlyList<string> GetHistoryTags()
    {
        var tags = new List<string>();
        var list = _historyType!.GetMethod("GetHistory", new[] { typeof(string) })!
            .Invoke(GetHistory(), new object[] { _appId });
        if (list is not System.Collections.IEnumerable items) return tags;
        foreach (var item in items)
        {
            if (_toastType!.GetProperty("Group")!.GetValue(item) as string != Group) continue;
            if (_toastType.GetProperty("Tag")!.GetValue(item) is string tag) tags.Add(tag);
        }
        return tags;
    }

    internal static string BuildXml(string title, string text, string attribution) =>
        "<toast>" +
        "<visual><binding template=\"ToastGeneric\">" +
        $"<text hint-maxLines=\"1\">{SecurityElement.Escape(title)}</text>" +
        $"<text>{SecurityElement.Escape(text)}</text>" +
        $"<text placement=\"attribution\">{SecurityElement.Escape(attribution)}</text>" +
        "</binding></visual>" +
        "</toast>";

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

    /// <summary>
    /// Target of the toast's Activated delegate. The event's delegate type is a
    /// WinRT generic that only exists at run time, so the handler is a method
    /// whose parameters are wide enough to bind to it.
    /// </summary>
    private sealed class ActivatedHandler
    {
        private readonly Action _activated;
        private readonly Action<string, Exception?> _log;

        public ActivatedHandler(Action activated, Action<string, Exception?> log)
        {
            _activated = activated;
            _log = log;
        }

        public void Invoke(object sender, object args)
        {
            try { _activated(); }
            catch (Exception ex) { _log("Windows notification click handler threw", ex); }
        }
    }
}
