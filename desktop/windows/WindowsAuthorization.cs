using System.ComponentModel;
using System.Diagnostics;

namespace Cage.Windows;

internal sealed class WindowsAuthorization : Form
{
    private readonly Guid vm;
    internal Guid VMID => vm;
    internal static WindowsAuthorization ForTarget(WindowsAuthorization? existing, Guid target)
    {
        if (existing != null && !existing.IsDisposed && existing.VMID == target) return existing;
        existing?.Close(); existing?.Dispose();
        return new WindowsAuthorization(target);
    }
    private WindowsConsoleControl? console;
    private readonly Panel viewport = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly Label status = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8), TextAlign = ContentAlignment.MiddleCenter, Text = "正在连接 Windows 客机画面…" };
    private readonly Button retry = new() { Text = "重新连接", AutoSize = true };
    private readonly Button access = new() { Text = "设置本机连接权限…", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly CancellationTokenSource lifetime = new();
    private DateTime deadline;
    private bool connecting, closed;
    private bool checking;
    private string attentionReason = "";
    private string ConnectedGuidance => attentionReason switch {
        "requires_gui_login" => "请在客机画面中自行登录或解锁，完成后自动返回 Cage。",
        "windows_application_prompt" => "请查看并处理应用提示，完成后自动返回 Cage。",
        "requires_system_attention" or "windows_security_prompt" => "请查看系统提示，并自行选择或输入所需信息。完成后自动返回 Cage。",
        _ => "请在客机画面中处理系统或应用提示，完成后点击“返回 Cage”。"
    };
    internal void SetAttention(string? reason)
    {
        attentionReason = reason ?? "";
        Text = attentionReason switch {
            "requires_gui_login" => "Cage · Windows 登录／解锁",
            "windows_application_prompt" => "Cage · Windows 应用提示",
            "requires_system_attention" or "windows_security_prompt" => "Cage · Windows 系统提示",
            _ => "Cage · Windows 系统／应用弹窗"
        };
        if (!connecting && console?.Connected == 1) status.Text = ConnectedGuidance;
    }

    internal WindowsAuthorization(Guid vm)
    {
        if (vm == Guid.Empty) throw new InvalidDataException("缺少虚拟机身份");
        this.vm = vm;
        SetAttention(null); StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1100, 800); MinimumSize = new Size(640, 480);
        ShowInTaskbar = false; AutoScaleMode = AutoScaleMode.Dpi;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10), WrapContents = true };
        var external = new Button { Text = "打开独立虚拟机窗口", AutoSize = true };
        var close = new Button { Text = "返回 Cage", AutoSize = true };
        buttons.Controls.AddRange([retry, access, external, close]);
        Controls.Add(viewport); Controls.Add(status); Controls.Add(buttons);
        retry.Click += (_, _) => Connect(); close.Click += (_, _) => Close();
        external.Click += (_, _) => {
            try {
                if (WindowsConsoleTarget.Load() != vm) throw new IOException("当前环境已变化，请重新打开客机窗口");
                var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "vmconnect.exe")) { UseShellExecute = true };
                info.ArgumentList.Add("localhost"); info.ArgumentList.Add("-G"); info.ArgumentList.Add(vm.ToString("D"));
                Process.Start(info)?.Dispose();
            } catch (Exception error) { status.Text = error.Message; }
        };
        access.Click += async (_, _) => {
            access.Enabled = false; retry.Enabled = false;
            status.Text = "请在 Windows 系统提示中确认。只设置本机账户连接当前 Cage 虚拟机的权限。";
            try {
                var granted = await WindowsConsoleAccess.Request(vm, lifetime.Token);
                if (closed) return;
                if (granted) Connect();
                else status.Text = "已取消 Windows 的连接权限授权。你可以重试，或返回 Cage。";
            } catch (Exception error) { if (!closed) status.Text = error.Message; }
            finally { if (!closed && !checking) { access.Enabled = true; retry.Enabled = !connecting; } }
        };
        Shown += (_, _) => { var area = Screen.FromControl(this).WorkingArea; Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height)); Connect(); };
        timer.Tick += (_, _) => {
            if (console == null) return;
            try {
                if (WindowsConsoleTarget.Load() != vm) { Close(); return; }
                if (console.Connected == 1 && connecting) {
                    console.FitToWindow();
                    connecting = false; retry.Enabled = true;
                    status.Text = ConnectedGuidance;
                    console.Focus();
                } else if (connecting && DateTime.UtcNow >= deadline) {
                    Fail("连接超时。可以设置本机连接权限后重试，或打开独立虚拟机窗口。");
                }
            } catch (Exception) { Fail("控制台连接已断开，请重新连接。"); }
        };
        FormClosed += (_, _) => { closed = true; lifetime.Cancel(); timer.Stop(); ReleaseConsole(); };
    }

    private void ReleaseConsole()
    {
        var previous = console; console = null;
        if (previous != null) { viewport.Controls.Remove(previous); previous.Dispose(); }
    }
    private void Fail(string message)
    {
        if (closed) return;
        connecting = false; timer.Stop(); retry.Enabled = true; status.Text = message;
        ReleaseConsole();
    }
    private async void Connect()
    {
        if (checking || closed) return;
        checking = true; retry.Enabled = false; access.Enabled = false;
        timer.Stop(); connecting = false; ReleaseConsole();
        try {
            if (WindowsConsoleTarget.Load() != vm) throw new IOException("当前 Windows 环境已变化，请返回 Cage 后重试");
            status.Text = "正在检查 Windows 环境状态…";
            var inspected = await Backend.Json(["agent", "call", "status"], lifetime.Token, deadline: TimeSpan.FromSeconds(5));
            if (closed) return;
            if (inspected.Exit != 0) throw new IOException("无法检查 Windows 环境，请返回 Cage 检查环境状态");
            WindowsConsoleTarget.RequireBackendRunning(inspected.Data);
            if (WindowsConsoleTarget.Load() != vm) { Close(); return; }
            access.Enabled = true;
            var current = new WindowsConsoleControl { Dock = DockStyle.Fill };
            console = current;
            ((ISupportInitialize)current).BeginInit(); viewport.Controls.Add(current); ((ISupportInitialize)current).EndInit();
            current.CreateControl(); current.Configure(vm);
            current.Disconnected += reason => {
                // COM callbacks must return before disposing their source control.
                if (!closed && IsHandleCreated) BeginInvoke(() => {
                    if (!closed && ReferenceEquals(console, current)) Fail($"连接未完成或已断开（代码 {reason}）。可先设置本机连接权限，再重新连接。");
                });
            };
            status.Text = "正在连接 Windows 客机画面…"; retry.Enabled = false;
            connecting = true; deadline = DateTime.UtcNow.AddSeconds(30);
            current.ConnectConsole(); timer.Start();
        } catch (Exception error) { Fail("无法连接客机画面：" + error.GetBaseException().Message); }
        finally { checking = false; if (!closed && !connecting) retry.Enabled = true; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !closed) { closed = true; lifetime.Cancel(); }
        if (disposing) { lifetime.Dispose(); timer.Dispose(); ReleaseConsole(); }
        base.Dispose(disposing);
    }
}
