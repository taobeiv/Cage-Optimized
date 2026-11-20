using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace Cage.Windows;

internal static class WindowsConsoleAccess
{
    internal static async Task<bool> Request(Guid vm, CancellationToken cancellation = default)
    {
        if (vm == Guid.Empty || WindowsConsoleTarget.Load() != vm) throw new InvalidDataException("当前 Windows 环境已变化，请重新打开授权窗口");
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidDataException("无法确认本机账户");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        info.ArgumentList.Add("--grant-console-access"); info.ArgumentList.Add(vm.ToString("D")); info.ArgumentList.Add(sid);
        try {
            var exit = await StartAndWait(() => Process.Start(info) ?? throw new IOException("无法打开连接权限设置"), cancellation);
            if (exit != 0) throw new IOException($"连接权限设置程序未完成（退出代码 0x{exit:X8}）。");
            return true;
        } catch (Win32Exception error) when (error.NativeErrorCode == 1223) { return false; }
    }

    // ShellExecute(runas) may block until the human dismisses host UAC.
    // Keep the UI thread responsive; closing the dialog abandons the later
    // process wait, but must not kill an authorized elevated helper.
    internal static Task<int> StartAndWait(Func<Process> launch, CancellationToken cancellation) => Task.Run(async () => {
        cancellation.ThrowIfCancellationRequested();
        using var process = launch();
        await process.WaitForExitAsync(cancellation);
        return process.ExitCode;
    }, cancellation);

    internal static int Run(string vmText, string owner)
    {
        try {
            if (!Guid.TryParseExact(vmText, "D", out var vm) || vm == Guid.Empty ||
                !Regex.IsMatch(owner, @"^S-1-(5-21|12-1)-[0-9-]+$") ||
                !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) return 2;
            _ = new SecurityIdentifier(owner);
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Cage.GrantConsoleAccess.ps1") ?? throw new IOException("缺少连接权限组件");
            using var reader = new StreamReader(resource);
            var script = "$ErrorActionPreference='Stop';$vm=[guid]'" + vm.ToString("D") + "';$owner='" + owner + "'\n" + reader.ReadToEnd() + "\n" + PrepareAndGrant;
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            using var process = new Process { StartInfo = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new IOException("连接权限设置超时，请重试"); }
            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0) throw new IOException("未能验证并设置此虚拟机的连接权限。请在 Cage 设置中检查 Windows 环境及管理助手。");
            return 0;
        } catch (Exception error) {
            MessageBox.Show(error.Message, "Cage · 连接权限", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private const string PrepareAndGrant = """
        $env:PSModulePath=Join-Path ([Environment]::SystemDirectory) 'WindowsPowerShell\v1.0\Modules'
        # Use the installed, administrator-protected manager, never a caller-supplied executable.
        $base=Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'Cage Host Manager'
        $directory=Join-Path $base $vm.ToString('D')
        $manager=Join-Path $directory 'Cage.HyperVManager.exe'
        for($p=$manager;$p;$p=[IO.Path]::GetDirectoryName($p)) {
            if((Get-Item -LiteralPath $p -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Manager path contains a link.'}
        }
        foreach($p in @($base,$directory,$manager)) {
            $acl=Get-Acl -LiteralPath $p
            if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Manager owner is not protected.'}
            foreach($ace in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
                if($ace.AccessControlType -eq 'Allow' -and ([int]$ace.FileSystemRights -band 0xD0156) -and
                    $ace.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Manager is writable by an untrusted principal.'}
            }
        }
        $raw=& $manager binding --vm-id $vm.ToString('D') --host-sid $owner
        if($LASTEXITCODE -ne 0){throw 'Protected binding verification failed.'}
        $binding=$raw|ConvertFrom-Json
        $raw=& $manager prepare --vm-id $vm.ToString('D') --host-sid $owner
        if($LASTEXITCODE -ne 0){throw 'Live VM verification failed.'}
        $live=$raw|ConvertFrom-Json
        Import-Module Hyper-V -ErrorAction Stop
        Grant-CageConsoleAccess $vm $owner $binding $live
        """;
}
