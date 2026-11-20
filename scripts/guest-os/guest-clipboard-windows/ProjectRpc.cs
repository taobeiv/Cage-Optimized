using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cage.GuestClipboard;

internal static class ProjectRpc
{
    private static readonly ConcurrentDictionary<string, Mount> Mounts = new(StringComparer.Ordinal);
    private static readonly SemaphoreSlim Slots = new(32, 32);
    private static readonly ProjectDriveLetters Drives = new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Cage", "Projects", "project-drives.json"), DriveOccupied);
    internal static event Action<string, string>? Revoked;
    private static readonly Regex ID = new("^[a-f0-9]{16}$", RegexOptions.CultureInvariant);
    private static readonly Regex Grant = new("^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Probe = new("^\\.cage-mount-probe-[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);
    [ModuleInitializer]
    internal static void Register() { if (ToolsPresent()) GuestOperations.Register("project-mount", Run); }
    internal static bool ToolsPresent()
    {
        try
        {
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = registry.OpenSubKey(@"SOFTWARE\WinFsp");
            var install = key?.GetValue("InstallDir") as string;
            return install is not null && File.Exists(Path.Combine(install, "bin", "winfsp-x64.dll")) &&
                File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SSHFS-Win", "bin", "sshfs-win.exe"));
        }
        catch { return false; }
    }
    internal static string Resolve(string id, string grant)
    {
        if (!ID.IsMatch(id) || !Grant.IsMatch(grant) || !Mounts.TryGetValue(id, out var mount) ||
            mount.Grant != grant || !mount.Ready || mount.Child.HasExited || mount.Lifetime.IsCancellationRequested)
            throw new InvalidDataException("project_grant_unavailable");
        return mount.Path;
    }
    internal static async Task Run(Stream stream, CancellationToken token, Func<bool> available)
    {
        if (!available()) throw new InvalidDataException("requires_gui_login");
        var value = await Protocol.Read(stream, 16384, token) ?? throw new EndOfStreamException();
        var id = value["id"]?.GetValue<string>() ?? "";
        var grant = value["grant"]?.GetValue<string>() ?? "";
        if (!ID.IsMatch(id) || !Grant.IsMatch(grant)) throw new InvalidDataException("project_grant_invalid");
        switch (value["action"]?.GetValue<string>())
        {
            case "mount": await Start(stream, id, grant, token, available); break;
            case "probe":
                if (!Mounts.TryGetValue(id, out var mount) || mount.Grant != grant || mount.Child.HasExited)
                    throw new InvalidDataException("project_grant_unavailable");
                var name = value["probe_name"]?.GetValue<string>() ?? "";
                if (!Probe.IsMatch(name)) throw new InvalidDataException("project_probe_invalid");
                try
                {
                    using var file = File.OpenRead(Path.Combine(mount.Path, name));
                    if (file.Length is < 1 or > 4096) throw new InvalidDataException("project_probe_invalid");
                    using var reader = new StreamReader(file, Protocol.Utf8);
                    var proof = await reader.ReadToEndAsync(token);
                    if (!available() || mount.Lifetime.IsCancellationRequested || mount.Child.HasExited) throw new InvalidDataException("requires_gui_login");
                    mount.Ready = true;
                    await Protocol.Write(stream, new() { ["mounted"] = true, ["id"] = id, ["path"] = mount.Path, ["proof"] = proof }, token);
                }
                catch (IOException) { await Protocol.Write(stream, new() { ["mounted"] = false }, token); }
                break;
            case "unmount":
                if (!Mounts.TryGetValue(id, out var selected) || selected.Grant != grant) throw new InvalidDataException("project_grant_unavailable");
                Revoke(id, selected);
                selected.Lifetime.Cancel();
                await selected.Done.Task.WaitAsync(token);
                await Protocol.Write(stream, new() { ["unmounted"] = true }, token); break;
            default: throw new InvalidDataException("project_operation_invalid");
        }
    }
    private static async Task Start(Stream stream, string id, string grant, CancellationToken token, Func<bool> available)
    {
        if (!await Slots.WaitAsync(0, token)) throw new InvalidDataException("project_limit");
        try { await StartCore(stream, id, grant, token, available); } finally { Slots.Release(); }
    }
    private static async Task StartCore(Stream stream, string id, string grant, CancellationToken token, Func<bool> available)
    {
        if (Mounts.TryGetValue(id, out var previous))
        {
            if (previous.Grant == grant) throw new InvalidDataException("project_mount_busy");
            Revoke(id, previous); previous.Lifetime.Cancel();
            await previous.Done.Task.WaitAsync(token);
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        using var identity = WindowsIdentity.GetCurrent();
        var parent = Path.Combine(home, "Cage", "Projects");
        foreach (var directory in new[] { Path.Combine(home, "Cage"), parent })
        {
            Directory.CreateDirectory(directory);
            var info = new DirectoryInfo(directory);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value != identity.User?.Value)
                throw new InvalidDataException("project_mount_parent_invalid");
        }
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SSHFS-Win", "bin", "sshfs-win.exe");
        if (!File.Exists(executable)) throw new InvalidDataException("project_tools_missing");
        using var drive = Drives.Acquire(id);
        var path = drive.Root;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // A network volume prefix supplies a canonical UNC path to realpath().
        // Directory mounts work for reads but fail Claude's folder admission check.
        // This prefix is a local WinFsp volume name, not an SMB/network connection.
        foreach (var arg in new[] { "cmd", ":/", path[..2], "-f", "-o", "slave,uid=-1,gid=-1,cache=no,nodev,nosuid,VolumePrefix=/cage-projects/" + id }) start.ArgumentList.Add(arg);
        using var child = new Process { StartInfo = start };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var mount = new Mount(grant, path, child, lifetime);
        if (!Mounts.TryAdd(id, mount)) throw new InvalidDataException("project_mount_busy");
        Task? input = null, output = null, stderr = null, exited = null;
        try
        {
            if (!available() || !child.Start()) throw new InvalidDataException("requires_gui_login");
            using var stop = lifetime.Token.Register(() => { try { child.Kill(true); } catch { } });
            await Protocol.Write(stream, new() { ["started"] = true, ["id"] = id, ["path"] = path }, lifetime.Token);
            input = stream.CopyToAsync(child.StandardInput.BaseStream, lifetime.Token);
            output = child.StandardOutput.BaseStream.CopyToAsync(stream, lifetime.Token);
            stderr = child.StandardError.BaseStream.CopyToAsync(Stream.Null, lifetime.Token);
            exited = child.WaitForExitAsync(lifetime.Token);
            await Task.WhenAny(input, output, exited);
        }
        finally
        {
            Revoke(id, mount);
            lifetime.Cancel();
            try { child.StandardInput.Close(); } catch { }
            try { if (!child.HasExited) child.Kill(true); await child.WaitForExitAsync(); } catch { }
            foreach (var task in new[] { input, output, stderr, exited }) if (task is not null) try { await task; } catch { }
            // A replacement mount may resume immediately when Done is completed.
            // Release its letter first, after the old SSHFS process has exited.
            drive.Dispose();
            mount.Done.TrySetResult();
        }
    }
    private static void Revoke(string id, Mount mount)
    {
        mount.Ready = false;
        if (!((ICollection<KeyValuePair<string, Mount>>)Mounts).Remove(new(id, mount))) return;
        if (Revoked is { } handlers)
            foreach (Action<string, string> handler in handlers.GetInvocationList())
                try { handler(id, mount.Grant); } catch { /* all consumers must receive revocation */ }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string device, char[] target, int length);
    private static bool DriveOccupied(char letter)
    {
        // Includes unready/removable devices that Directory.Exists misses.
        var buffer = new char[32768];
        return QueryDosDevice(letter + ":", buffer, buffer.Length) != 0 || Marshal.GetLastWin32Error() != 2;
    }
    private sealed class Mount(string grant, string path, Process child, CancellationTokenSource lifetime)
    {
        internal string Grant { get; } = grant;
        internal string Path { get; } = path;
        internal Process Child { get; } = child;
        internal CancellationTokenSource Lifetime { get; } = lifetime;
        internal readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Ready;
    }
}
