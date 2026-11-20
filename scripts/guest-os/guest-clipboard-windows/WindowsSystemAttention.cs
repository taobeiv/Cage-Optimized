using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cage.GuestClipboard;

internal static class WindowsSystemAttention
{
    private delegate bool EnumWindow(IntPtr window, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindow callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindow callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int count);
    [DllImport("kernel32.dll")] private static extern bool ProcessIdToSessionId(uint pid, out uint session);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
    private static readonly object gate = new();
    private static string previousReason = "";

    internal static bool Matches(string image, string title)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        if (!Path.IsPathFullyQualified(image) || !Path.GetFullPath(image).StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return false;
        return Path.GetFileName(image).Equals("consent.exe", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(image).Equals("CredentialUIBroker.exe", StringComparison.OrdinalIgnoreCase) ||
            title is "Windows Security" or "Windows 安全中心" or "Windows 安全性" or "Windows Defender Firewall" or "Windows Defender 防火墙";
    }

    internal sealed record WindowMetadata(bool SameSession, bool Visible, bool Enabled, bool OwnerDisabled,
        string ClassName, uint Style, uint ExStyle, string[] ChildClasses, int[] ChildIds, bool Security = false);

    internal static string Classify(WindowMetadata window)
    {
        if (!window.SameSession || !window.Visible || !window.Enabled) return "";
        if (window.ClassName is "#32768" or "tooltips_class32" or "SysShadow" or "IME" || (window.ExStyle & 0x08000000) != 0) return "";
        // Shell/common-file-dialog structure; never inspect edit contents or localized titles.
        if (window.ChildClasses.Any(c => c is "DUIViewWndClassName" or "SHELLDLL_DefView" or "SHBrowseForFolder ShellNameSpace Control") ||
            // Common Open/Save dialogs use edt1 or cmb13 for filename, and cmb1 for file type.
            ((window.ChildIds.Contains(0x480) || window.ChildIds.Contains(0x47c)) && window.ChildIds.Contains(0x470))) return "";
        if (window.Security) return "windows_security_prompt";
        return window.OwnerDisabled && (window.ClassName == "#32770" || (window.Style & 0x80000000) != 0)
            ? "windows_application_prompt" : "";
    }

    internal static string SelectReason(IEnumerable<WindowMetadata> windows)
    {
        var result = "";
        foreach (var window in windows)
        {
            var reason = Classify(window);
            if (reason == "windows_security_prompt") return reason;
            if (reason != "") result = reason;
        }
        return result;
    }

    internal static string ResolveScan(string previous, string detected, bool complete) =>
        detected != "" || complete ? detected : previous;

    private static string ClassName(IntPtr window)
    {
        var name = new StringBuilder(256);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private static bool SameSession(IntPtr window, uint session, out uint pid) =>
        GetWindowThreadProcessId(window, out pid) != 0 && ProcessIdToSessionId(pid, out var actual) && actual == session;

    private static bool SecurityPrompt(IntPtr window, uint pid)
    {
        var handle = OpenProcess(0x1000, false, pid); // Query only; no input, automation or injection.
        if (handle == IntPtr.Zero) return false;
        try
        {
            var image = new StringBuilder(32768); uint size = (uint)image.Capacity;
            if (!QueryFullProcessImageName(handle, 0, image, ref size)) return false;
            // Only system prompt detection reads a top-level caption, never child control text.
            var title = new StringBuilder(256); GetWindowText(window, title, title.Capacity);
            return Matches(image.ToString(), title.ToString());
        }
        finally { CloseHandle(handle); }
    }

    internal static string PendingReason()
    {
        lock (gate)
        {
            try
            {
                using var current = Process.GetCurrentProcess();
                var session = checked((uint)current.SessionId);
                var windows = new List<WindowMetadata>();
                var timer = Stopwatch.StartNew();
                var count = 0; var complete = true;
                var enumerated = EnumWindows((window, parameter) =>
                {
                    if (++count > 512 || timer.ElapsedMilliseconds > 200) { complete = false; return false; }
                    if (!IsWindowVisible(window) || !IsWindowEnabled(window) || !SameSession(window, session, out var pid)) return true;
                    if (DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                    var owner = GetWindow(window, 4); // GW_OWNER, retained even after foreground changes.
                    var blocked = owner != IntPtr.Zero && SameSession(owner, session, out _) && !IsWindowEnabled(owner);
                    var security = SecurityPrompt(window, pid);
                    if (!blocked && !security) return true;
                    var classes = new List<string>(); var ids = new List<int>(); var children = 0; var childComplete = true;
                    EnumChildWindows(window, (child, _) =>
                    {
                        if (++children > 256 || timer.ElapsedMilliseconds > 200) { childComplete = false; return false; }
                        classes.Add(ClassName(child)); ids.Add(GetDlgCtrlID(child));
                        return true;
                    }, IntPtr.Zero);
                    // Incomplete exclusion evidence must not newly classify a possible file chooser.
                    if (!childComplete) { complete = false; return true; }
                    windows.Add(new(true, true, true, blocked, ClassName(window), unchecked((uint)GetWindowLong(window, -16)),
                        unchecked((uint)GetWindowLong(window, -20)), classes.ToArray(), ids.ToArray(), security));
                    return true;
                }, IntPtr.Zero);
                previousReason = ResolveScan(previousReason, SelectReason(windows), complete && enumerated);
            }
            catch { /* Unknown observation is not evidence that the user's prompt was resolved. */ }
            return previousReason;
        }
    }
}
