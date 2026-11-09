using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MyPLaMo;

sealed record ComputeDevice(string Key, string Backend, string Id, string Name, int Index, bool Integrated, int Ordinal = 0)
{
    public static ComputeDevice Cpu { get; } = new("cpu", "cpu", "none", "CPU", 0, false);
    public string Label => Backend == "cpu" ? "CPUのみ" : $"{Name}{(Ordinal > 0 ? $"〈同名GPU {Ordinal + 1}〉" : "")}{(Integrated ? "〈内蔵〉" : "")} / {(Backend == "cuda" ? "CUDA" : "Vulkan")}";
}

sealed record DeviceCatalog(ComputeDevice[] Devices, string[] Errors)
{
    public ComputeDevice[] Candidates(string selection)
    {
        // Explicit choices never spill over onto another GPU: CPU is the only fallback.
        var gpu = selection == "auto"
            ? Devices.OrderBy(x => x.Integrated).ThenBy(x => x.Backend == "cuda" ? 0 : 1)
            : Devices.Where(x => x.Key == selection);
        return gpu.Append(ComputeDevice.Cpu).ToArray();
    }
}

static class ComputeDevices
{
    public static string DefaultRoot => Path.Combine(AppContext.BaseDirectory, "engine");

    public static ProcessStartInfo StartInfo(string root, string backend)
    {
        var directory = Path.Combine(root, backend);
        var info = new ProcessStartInfo(Path.Combine(directory, "llama-server.exe")) {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        // Device indices must mean the same thing in discovery and inference.
        info.Environment.Remove("CUDA_VISIBLE_DEVICES");
        info.Environment.Remove("GGML_VK_VISIBLE_DEVICES");
        return info;
    }

    public static async Task<DeviceCatalog> Discover(string root, CancellationToken ct)
    {
        var queries = await Task.WhenAll(Query(root, "cuda", ct), Query(root, "vulkan", ct));
        return new(queries.SelectMany(x => x.Devices).ToArray(), queries.SelectMany(x => x.Errors).ToArray());
    }

    static async Task<DeviceCatalog> Query(string root, string backend, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = new Process { StartInfo = StartInfo(root, backend) };
        foreach (var arg in new[] { "--verbosity", "5", "--list-devices" }) process.StartInfo.ArgumentList.Add(arg);
        try {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); }
            finally {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); }
                await Task.WhenAll(output, errors);
            }
            var stdout = await output; var stderr = await errors;
            if (process.ExitCode != 0) return new([], [$"{backend.ToUpperInvariant()}を確認できませんでした（終了コード {process.ExitCode}）。"]);
            return new(Parse(backend, stdout, stderr), []);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            return new([], [$"{backend.ToUpperInvariant()}の確認がタイムアウトしました。"]);
        } catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) {
            return new([], [$"{backend.ToUpperInvariant()}を確認できませんでした。"]);
        }
    }

    internal static ComputeDevice[] Parse(string backend, string stdout, string stderr)
    {
        var devices = new List<ComputeDevice>();
        foreach (Match match in Regex.Matches(stdout, @"^\s*(?<id>CUDA\d+|Vulkan\d+):\s*(?<name>.+) \(\d+ MiB, \d+ MiB free\)\s*$", RegexOptions.Multiline)) {
            var id = match.Groups["id"].Value;
            if (!id.StartsWith(backend, StringComparison.OrdinalIgnoreCase)) continue;
            var name = match.Groups["name"].Value.Trim();
            int index = int.Parse(Regex.Match(id, @"\d+$").Value, CultureInfo.InvariantCulture);
            int ordinal = devices.Count(x => x.Name == name);
            // Names survive a driver changing enumeration order. Identical cards use an occurrence number.
            var key = $"{backend}:{Uri.EscapeDataString(name)}:{ordinal}";
            bool integrated = backend == "vulkan" && Regex.IsMatch(stderr, $@"ggml_vulkan: {index} = [^\r\n]+\| uma: 1 \|");
            devices.Add(new(key, backend, id, name, index, integrated, ordinal));
        }
        return devices.ToArray();
    }
}
