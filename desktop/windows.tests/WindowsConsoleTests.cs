using System.Runtime.ExceptionServices;
using System.Text.Json;
using Cage.Windows;

internal static class WindowsConsoleTests
{
    internal static void Run()
    {
        var vm = Guid.Parse("564eb004-cf4a-4143-9984-e26c4204ae19");
        var valid = JsonSerializer.Serialize(new { version = 1, vm_id = vm, mode = "hyperv" });
        WindowsConsoleTarget.RequireBackendRunning(JsonSerializer.SerializeToElement(new { status = "completed", data = new { guest_system = "windows-11", vm_running = true, ready = false } }));
        foreach (var response in new[] { "{}", "{\"status\":\"error\"}", "{\"status\":\"completed\",\"data\":{\"guest_system\":\"windows-remote\",\"vm_running\":true}}", "{\"status\":\"completed\",\"data\":{\"guest_system\":\"windows-11\",\"vm_running\":false}}" }) {
            try { WindowsConsoleTarget.RequireBackendRunning(JsonDocument.Parse(response).RootElement); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Unverified backend state admitted a console");
        }
        WindowsConsoleTarget.RequireRunning(JsonSerializer.SerializeToElement(new { lookup = "found", vm = new { id = vm, state = "Running" } }));
        foreach (var state in new[] { "Off", "Saved", "Stopping", "Paused", "" }) {
            try { WindowsConsoleTarget.RequireRunning(JsonSerializer.SerializeToElement(new { lookup = "found", vm = new { state } })); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Non-running guest reached connection: " + state);
        }
        if (WindowsConsoleTarget.Parse(valid) != vm) throw new Exception("Local guest identity changed");
        foreach (var profile in new[] { "{}", "null", "[]", "{", "{\"version\":2}",
            valid.Replace("\"version\":1", "\"version\":\"1\""), valid.Replace("\"mode\":\"hyperv\"", "\"mode\":null"),
            valid.Replace("hyperv", "remote"), valid.Replace("hyperv", "other"),
            valid.Replace(vm.ToString(), Guid.Empty.ToString()), valid.Replace(vm.ToString(), "localhost;cmd") })
        {
            try { WindowsConsoleTarget.Parse(profile); }
            catch (InvalidDataException) { continue; }
            throw new Exception("Invalid or remote guest accepted: " + profile);
        }
        // Exercise the actual ActiveX property boundary on an STA. No Connect,
        // credentials, keyboard injection, or changes to a running VM.
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                using var form = new Form { ShowInTaskbar = false, Opacity = 0 };
                using var host = new WindowsConsoleControl();
                ((System.ComponentModel.ISupportInitialize)host).BeginInit();
                form.Controls.Add(host);
                ((System.ComponentModel.ISupportInitialize)host).EndInit();
                form.Show(); Application.DoEvents();
                host.Configure(vm);
                var nativeControl = host.GetOcx() ?? throw new Exception("Console control missing");
                var extended = (IConsoleExtendedSettings)nativeControl;
                System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(extended.GetProperty("DisableCredentialsDelegation", out var noDelegation));
                if (noDelegation is not true) throw new Exception("Console attempted host credential delegation");
                var advanced = nativeControl.GetType().InvokeMember("AdvancedSettings7", System.Reflection.BindingFlags.GetProperty, null, nativeControl, null)!;
                advanced.GetType().InvokeMember("SmartSizing", System.Reflection.BindingFlags.SetProperty, null, advanced, [false]);
                host.FitToWindow();
                if (!Convert.ToBoolean(advanced.GetType().InvokeMember("SmartSizing", System.Reflection.BindingFlags.GetProperty, null, advanced, null)))
                    throw new Exception("Console fit was not restored after desktop negotiation");
                if (host.Connected != 0) throw new Exception("Configuration unexpectedly connected");
                form.Close();
                using var initial = new WindowsAuthorization(vm);
                if (!initial.Text.Contains("系统／应用")) throw new Exception("Manual console title incorrectly implies system authorization");
                initial.SetAttention("requires_gui_login");
                if (!initial.Text.Contains("登录") || !initial.Text.Contains("解锁")) throw new Exception("Locked guest presented as ordinary consent");
                initial.SetAttention("windows_application_prompt");
                if (!initial.Text.Contains("应用提示")) throw new Exception("Application prompt presented as system consent");
                initial.SetAttention("unknown");
                if (!initial.Text.Contains("系统／应用")) throw new Exception("Unknown reason lost neutral wording");
                if (!ReferenceEquals(initial, WindowsAuthorization.ForTarget(initial, vm))) throw new Exception("Same-target window replaced unnecessarily");
                using var replacement = WindowsAuthorization.ForTarget(initial, Guid.Parse("be625354-9f41-48ab-a957-68339bc647c9"));
                if (!initial.IsDisposed || replacement.VMID == vm) throw new Exception("Old guest console survived a binding change");
            } catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Console control test timed out");
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();
        var callerThread = Environment.CurrentManagedThreadId;
        var launchThread = callerThread;
        // A ShellExecute UAC prompt can block process creation. It must not
        // block the caller or the window's message/binding-validation loop.
        var pending = WindowsConsoleAccess.StartAndWait(() => {
            launchThread = Environment.CurrentManagedThreadId; entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(3))) throw new Exception("UI caller was blocked by elevation");
            cancel.Token.ThrowIfCancellationRequested();
            throw new Exception("Canceled launch continued");
        }, cancel.Token);
        if (!entered.Wait(TimeSpan.FromSeconds(3)) || launchThread == callerThread || pending.IsCompleted)
            throw new Exception("Elevation did not leave the caller responsive");
        cancel.Cancel(); release.Set();
        try { pending.GetAwaiter().GetResult(); throw new Exception("Closed popup retained elevation wait"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("Windows console: invalid/remote identities rejected; native control configured without connecting.");
    }
}
