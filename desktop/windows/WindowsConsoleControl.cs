using System.Reflection;
using System.Runtime.InteropServices;

namespace Cage.Windows;

[ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IConsoleExtendedSettings
{
    [PreserveSig] int SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [In, MarshalAs(UnmanagedType.Struct)] ref object value);
    [PreserveSig] int GetProperty([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.Struct)] out object value);
}

internal sealed class WindowsConsoleControl : AxHost
{
    // Microsoft's Windows 10+ native client, never exposed as a scriptable web object.
    internal WindowsConsoleControl() : base("A0C63C30-F08D-4AB4-907C-34905D770C7D") {}
    private static readonly Guid EventInterface = new("336d5562-efa8-482e-8cb3-c5c0fc7a7db6");
    internal event Action<int>? Disconnected;
    private Action<int>? disconnected;
    private Action<int, int>? desktopResized;
    private object? control;
    internal int Connected => control == null ? 0 : Convert.ToInt32(Get(control, "Connected"));
    private static object Get(object target, string name) => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null)!;
    private static void Set(object target, string name, object value) => target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);
    private static void Call(object target, string name) => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, null);

    internal void Configure(Guid vm)
    {
        if (vm == Guid.Empty) throw new InvalidDataException("缺少虚拟机身份");
        control = GetOcx() ?? throw new InvalidOperationException("控制台尚未加载");
        Set(control, "Server", "localhost");
        var advanced = Get(control, "AdvancedSettings7");
        Set(advanced, "RDPPort", 2179);
        Set(advanced, "PCB", vm.ToString("D")); // No EnhancedMode: the current physical console, including secure desktop.
        Set(advanced, "EnableCredSspSupport", true);
        // Hyper-V's preconnection transport expects CredSSP directly, not a
        // normal RDP security-negotiation PDU before its console handshake.
        Set(advanced, "NegotiateSecurityLayer", false);
        Set(advanced, "AuthenticationServiceClass", "Microsoft Virtual Console Service");
        object noDelegation = true;
        Marshal.ThrowExceptionForHR(((IConsoleExtendedSettings)control).SetProperty("DisableCredentialsDelegation", ref noDelegation));
        Set(advanced, "SmartSizing", true);
        Set(advanced, "EnableAutoReconnect", false);
        foreach (var property in new[] { "RedirectDrives", "RedirectPrinters", "RedirectPorts", "RedirectSmartCards", "RedirectClipboard", "RedirectDevices", "RedirectPOSDevices" })
            Set(advanced, property, false);
        Set(Get(control, "SecuredSettings2"), "KeyboardHookMode", 0);
        if (disconnected == null) {
            disconnected = reason => Disconnected?.Invoke(reason);
            ComEventsHelper.Combine(control, EventInterface, 4, disconnected);
            desktopResized = (_, _) => QueueFit();
            ComEventsHelper.Combine(control, EventInterface, 12, desktopResized);
        }
    }

    internal void FitToWindow()
    {
        if (control == null) return;
        // Hyper-V supplies the actual console dimensions after connection.
        // Reapply client-side fitting after that negotiation and on resize.
        var advanced = Get(control, "AdvancedSettings7");
        Set(advanced, "SmartSizing", false);
        Set(advanced, "SmartSizing", true);
    }
    private void QueueFit()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() => {
            if (IsDisposed || control == null) return;
            try { if (Connected == 1) FitToWindow(); }
            catch (COMException) { } catch (TargetInvocationException) { }
        });
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (control != null) QueueFit(); }

    internal void ConnectConsole() { if (control == null) throw new InvalidOperationException("控制台尚未初始化"); Call(control, "Connect"); }
    internal void DisconnectConsole() { if (control != null && Connected != 0) Call(control, "Disconnect"); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && control != null) {
            try {
                DisconnectConsole();
                if (disconnected != null) ComEventsHelper.Remove(control, EventInterface, 4, disconnected);
                if (desktopResized != null) ComEventsHelper.Remove(control, EventInterface, 12, desktopResized);
            } catch (COMException) { } catch (TargetInvocationException) { } catch (InvalidComObjectException) { }
            finally { disconnected = null; desktopResized = null; control = null; }
        }
        base.Dispose(disposing);
    }
}
