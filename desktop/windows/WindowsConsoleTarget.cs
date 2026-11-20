using System.Text.Json;

namespace Cage.Windows;

internal static class WindowsConsoleTarget
{
    internal static void RequireBackendRunning(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || response.Text("status") != "completed" ||
            !response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
            data.Text("guest_system") != "windows-11" || !data.TryGetProperty("vm_running", out var running) || running.ValueKind != JsonValueKind.True)
            throw new InvalidDataException("无法确认本机 Windows 环境正在运行，请返回 Cage 检查环境状态。");
    }
    internal static void RequireRunning(JsonElement inspection)
    {
        if (inspection.Text("lookup") != "found" || !inspection.TryGetProperty("vm", out var vm) || vm.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("无法核对虚拟机状态，请在 Cage 设置中检查环境");
        if (vm.Text("state") != "Running")
            throw new InvalidDataException("Windows 环境尚未运行。请返回 Cage 启动环境，再处理系统授权。");
    }

    internal static Guid Parse(string json)
    {
        try {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object ||
                !value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
                (value.TryGetProperty("mode", out var mode) && mode.ValueKind != JsonValueKind.String) ||
                value.Text("mode") is not ("" or "hyperv") ||
                !Guid.TryParseExact(value.Text("vm_id"), "D", out var vm) || vm == Guid.Empty)
                throw new InvalidDataException("此环境没有可连接的本机 Windows 虚拟机");
            return vm;
        } catch (JsonException) { throw new InvalidDataException("Windows 环境配置无效，请在设置中检查环境"); }
    }

    internal static Guid Load()
    {
        if (Backend.ClientOnly) throw new InvalidDataException("请在运行虚拟机的电脑上处理系统授权");
        var path = Path.Combine(Backend.Root, "windows-guest.json");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 65536) throw new InvalidDataException("Windows 环境配置无效");
        using var reader = new StreamReader(input);
        return Parse(reader.ReadToEnd());
    }
}
