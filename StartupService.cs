using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;

namespace FRPMonitor;

public static class StartupService
{
    private const string Marker = "FRPMonitor";
    private static string UserSid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    }
    private static string TaskName => "FRPMonitor-" + UserSid;
    private static bool IsCurrentUser(string? account)
    {
        if (account == UserSid) return true;
        if (string.IsNullOrWhiteSpace(account)) return false;
        try { return ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value == UserSid; }
        catch (IdentityNotMappedException) { return false; }
    }
    private sealed class ComScope : IDisposable
    {
        private readonly List<object> objects = new();
        public dynamic Keep(object instance) { if (!objects.Exists(o => ReferenceEquals(o, instance))) objects.Add(instance); return instance; }
        public void Dispose()
        {
            for (int i = objects.Count - 1; i >= 0; i--)
                if (Marshal.IsComObject(objects[i])) Marshal.FinalReleaseComObject(objects[i]);
        }
    }
    private static dynamic Connect(ComScope scope)
    {
        dynamic service = scope.Keep(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!);
        service.Connect(); return scope.Keep(service.GetFolder("\\"));
    }
    private static dynamic? GetOwnedTask(ComScope scope, dynamic folder, string name)
    {
        dynamic task;
        try { task = scope.Keep(folder.GetTask(name)); }
        catch (Exception e) when (e.HResult == unchecked((int)0x80070002) || e.HResult == unchecked((int)0x8004130F)) { return null; }
        dynamic definition = scope.Keep(task.Definition);
        dynamic info = scope.Keep(definition.RegistrationInfo);
        if ((string)info.Source != Marker) throw new InvalidOperationException("同名登录任务不属于 FRPMonitor，未进行修改。");
        return task;
    }
    public static bool IsEnabled() => IsEnabled(TaskName);
    private static bool IsEnabled(string name)
    {
        using var scope = new ComScope(); dynamic folder = Connect(scope);
        dynamic? task = GetOwnedTask(scope, folder, name);
        return task is not null && (bool)task!.Enabled;
    }
    public static void SetEnabled(bool enabled) => SetEnabled(enabled, TaskName);
    private static void SetEnabled(bool enabled, string name)
    {
        using var scope = new ComScope();
        dynamic service = scope.Keep(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!);
        service.Connect(); dynamic folder = scope.Keep(service.GetFolder("\\"));
        dynamic? existing = GetOwnedTask(scope, folder, name);
        if (!enabled) { if (existing != null) folder.DeleteTask(name, 0); return; }
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序文件位置。");
        var arguments = "--startup";
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            arguments = "\"" + Path.GetFullPath(Environment.GetCommandLineArgs()[0]) + "\" --startup";
        if (!File.Exists(executable)) throw new FileNotFoundException("程序文件不存在。", executable);
        dynamic definition = scope.Keep(service.NewTask(0));
        dynamic info = scope.Keep(definition.RegistrationInfo);
        info.Source = Marker; info.Description = "FRP 流量监控：当前用户登录 Windows 后自动采集。";
        dynamic principal = scope.Keep(definition.Principal);
        principal.UserId = UserSid; principal.LogonType = 3; principal.RunLevel = 1;
        dynamic settings = scope.Keep(definition.Settings);
        settings.Enabled = true; settings.StartWhenAvailable = true;
        settings.DisallowStartIfOnBatteries = false; settings.StopIfGoingOnBatteries = false;
        settings.ExecutionTimeLimit = "PT0S"; settings.MultipleInstances = 2;
        dynamic triggers = scope.Keep(definition.Triggers);
        dynamic logon = scope.Keep(triggers.Create(9)); // TASK_TRIGGER_LOGON
        logon.UserId = UserSid; logon.Enabled = true; logon.Delay = "PT10S";
        dynamic actions = scope.Keep(definition.Actions);
        dynamic action = scope.Keep(actions.Create(0)); // TASK_ACTION_EXEC
        action.Path = executable; action.Arguments = arguments; action.WorkingDirectory = Path.GetDirectoryName(executable);
        scope.Keep(folder.RegisterTaskDefinition(name, definition, 6, UserSid, null, 3, null)); // CREATE_OR_UPDATE, INTERACTIVE_TOKEN
    }
    public static int RunSelfTest(string reportPath)
    {
        var testName = "FRPMonitor-Test-" + Guid.NewGuid().ToString("N");
        var checks = new List<string>(); string? error = null, cleanupError = null;
        try
        {
            var original = IsEnabled();
            SetEnabled(true, testName);
            if (!IsEnabled(testName)) throw new Exception("登录任务未启用。"); checks.Add("enable");
            using (var scope = new ComScope())
            {
                dynamic folder = Connect(scope); dynamic task = GetOwnedTask(scope, folder, testName)!;
                var xml = XDocument.Parse((string)task.Xml);
                XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
                var root = xml.Root!; var principal = root.Element(ns + "Principals")!.Element(ns + "Principal")!;
                var trigger = root.Element(ns + "Triggers")!.Element(ns + "LogonTrigger");
                var action = root.Element(ns + "Actions")!.Element(ns + "Exec")!; var settings = root.Element(ns + "Settings")!;
                if ((string?)principal.Element(ns + "RunLevel") != "HighestAvailable" || (string?)principal.Element(ns + "LogonType") != "InteractiveToken" || trigger == null || !IsCurrentUser((string?)trigger.Element(ns + "UserId")) || !IsCurrentUser((string?)principal.Element(ns + "UserId")))
                    throw new Exception("登录任务验证失败：RunLevel=" + (string?)principal.Element(ns + "RunLevel") + ", LogonType=" + (string?)principal.Element(ns + "LogonType") + ", TriggerUserMatches=" + IsCurrentUser((string?)trigger?.Element(ns + "UserId")));
                if ((string?)action.Element(ns + "Command") != Environment.ProcessPath || !((string?)action.Element(ns + "Arguments") ?? "").EndsWith("--startup"))
                    throw new Exception("启动路径或参数不正确。");
                if ((string?)settings.Element(ns + "DisallowStartIfOnBatteries") != "false" || (string?)settings.Element(ns + "StopIfGoingOnBatteries") != "false" || (string?)settings.Element(ns + "ExecutionTimeLimit") != "PT0S")
                    throw new Exception("任务运行条件不正确。");
                checks.Add("user logon / elevated interactive launch / command / no battery or runtime stop");
            }
            SetEnabled(false, testName);
            if (IsEnabled(testName)) throw new Exception("登录任务未移除。"); checks.Add("disable");
            if (IsEnabled() != original) throw new Exception("正式自启设置发生变化。"); checks.Add("production startup unchanged");
        }
        catch (Exception e) { error = e.ToString(); }
        finally { try { SetEnabled(false, testName); } catch (Exception e) { cleanupError = e.ToString(); } }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { result = error == null && cleanupError == null ? "PASS" : "FAIL", error, cleanupError, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return error == null && cleanupError == null ? 0 : 1;
    }
}
