using MyPLaMo;
using System.Diagnostics;
using System.Text.Json;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
var fixture = ComputeDevices.Parse("vulkan", "Available devices:\n  Vulkan0: AMD Radeon(TM) Graphics (8192 MiB, 7000 MiB free)\n  Vulkan1: NVIDIA GeForce RTX 5090 (32000 MiB, 31000 MiB free)\n", "ggml_vulkan: 0 = AMD Radeon(TM) Graphics (AMD) | uma: 1 | fp16: 1\n");
Check(fixture.Length == 2 && fixture[0].Integrated && !fixture[1].Integrated, "Parse real-format names and shared-memory GPU classification");
var reordered = ComputeDevices.Parse("vulkan", "  Vulkan0: NVIDIA GeForce RTX 5090 (32000 MiB, 31000 MiB free)\n", "");
Check(fixture[1].Key == reordered[0].Key && fixture[1].Index != reordered[0].Index, "Persist GPU identity across enumeration-order changes");
var cudaFixture = ComputeDevices.Parse("cuda", "  CUDA0: NVIDIA GeForce RTX 5090 (32000 MiB, 31000 MiB free)\n", "");
var sample = new DeviceCatalog([.. fixture, .. cudaFixture], []);
Check(sample.Candidates("auto")[0].Backend == "cuda" && sample.Candidates("auto")[1] == fixture[1], "Auto prioritizes CUDA then discrete Vulkan before integrated Vulkan");
Check(sample.Candidates(fixture[0].Key).SequenceEqual(new[] { fixture[0], ComputeDevice.Cpu }), "Explicit integrated GPU can only fall back to CPU");
Check(sample.Candidates("missing-gpu").SequenceEqual(new[] { ComputeDevice.Cpu }), "Missing selected GPU never uses a different GPU");
Check(JsonSerializer.Deserialize<Preferences>("{\"GPU\":false}")!.ExecutionDevice == "cpu", "Existing CPU-only settings remain CPU-only");
Check(JsonSerializer.Deserialize<Preferences>("{\"GPU\":true}")!.ExecutionDevice == "auto", "Existing GPU-priority settings migrate to automatic selection");
var saved = new Preferences { GPU = true, ComputeDevice = fixture[0].Key, ComputeDeviceLabel = fixture[0].Label };
Check(JsonSerializer.Deserialize<Preferences>(JsonSerializer.Serialize(saved))!.ExecutionDevice == fixture[0].Key, "Explicit device setting survives serialization");
if (args.Length == 0) { Console.WriteLine($"PASS: {passed} device selection checks"); return; }

var root = Path.GetFullPath(args[0]);
var models = Path.GetFullPath(args[1]);
var reportPath = Path.GetFullPath(args[2]);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(30));
var ct = deadline.Token;
using var engine = new Engine(root);
var progress = new Progress<string>(Console.WriteLine);
var catalog = await engine.ListDevices(ct);
foreach (var d in catalog.Devices) Console.WriteLine($"DEVICE {d.Key} => {d.Label} (backend index {d.Index})");
Check(catalog.Devices.Any(x => x.Backend == "cuda" && x.Name.Contains("NVIDIA")), "CUDA detects the NVIDIA GPU");
Check(catalog.Devices.Any(x => x.Integrated), "Vulkan detects the integrated AMD GPU");
var rows = new List<object>();
var sources = new[] {
    "The sky is blue.",
    "This application translates text locally on your computer. You can choose which graphics processor to use. When creating images, select the CPU to keep the graphics memory available for image generation."
};
foreach (var id in new[] { "0.8b", "2b" }) {
    var model = Model.All.Single(x => x.id == id);
    foreach (var choice in catalog.Devices.Append(ComputeDevice.Cpu)) {
        engine.Dispose();
        var watch = Stopwatch.StartNew();
        await engine.Load(Path.Combine(models, model.file), choice.Key, progress, ct);
        double loadSeconds = watch.Elapsed.TotalSeconds;
        Check(engine.Device == (choice.Backend == "cpu" ? "CPU" : choice.Label), $"{id} runs on requested {choice.Label}");
        var processId = engine.ProcessId;
        await engine.Load(Path.Combine(models, model.file), choice.Key, progress, ct);
        Check(engine.ProcessId == processId, "Repeated translation reuses the selected device's server");
        // One warm-up translation, followed by 3 fresh requests with the same application settings.
        var warm = await engine.Translate(sources[0], "en", "ja", "polite", ct);
        Check(warm.Contains('青'), $"{choice.Label} translates English to Japanese");
        var timings = new List<double>(); string translated = "";
        for (int run = 0; run < 3; run++) {
            watch.Restart(); translated = await engine.Translate(sources[1], "en", "ja", "polite", ct);
            timings.Add(watch.Elapsed.TotalSeconds);
        }
        Check(translated.Length > 20 && translated.Any(c => c >= '\u3040' && c <= '\u30ff'), "Benchmark returns Japanese text");
        var serverPath = Process.GetProcessById(processId!.Value).MainModule!.FileName;
        Check(Path.GetDirectoryName(serverPath)!.Equals(Path.Combine(root, choice.Backend), StringComparison.OrdinalIgnoreCase), "Backend is isolated in its own runtime directory");
        rows.Add(new { model = id, device = engine.Device, integrated = choice.Integrated, loadSeconds, timings, medianSeconds = timings.Order().ElementAt(1), translated });
        Console.WriteLine($"BENCH {id} {engine.Device}: load {loadSeconds:F2}s; median {timings.Order().ElementAt(1):F3}s");
        engine.Dispose();
        Check(!Process.GetProcessesByName("llama-server").Any(x => { using (x) return x.Id == processId; }), "Disposing releases the inference process and its GPU allocations");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed, devices = catalog.Devices, benchmarks = rows }, new JsonSerializerOptions { WriteIndented = true }), ct);
    }
}

var small = Path.Combine(models, Model.All.Single(x => x.id == "0.8b").file);
await engine.Load(small, "auto", progress, ct);
Check(engine.Device.Contains("CUDA"), "Automatic mode really selects CUDA");
await engine.Load(small, "not-connected-gpu", progress, ct);
Check(engine.Device.StartsWith("CPU") && engine.Device.Contains("指定GPU"), "Disconnected GPU falls back to CPU with a visible reason");
Check((await engine.Translate(sources[0], "en", "ja", "", ct)).Contains('青'), "CPU fallback translates successfully");
engine.Dispose();
// Simulate a machine where the CUDA backend is unavailable; restore before any other work.
var cudaPath = Path.Combine(root, "cuda"); var hidden = Path.Combine(root, "cuda-test-disabled");
Directory.Move(cudaPath, hidden);
try {
    await engine.Load(small, "auto", progress, ct);
    Check(engine.Device.Contains("Vulkan") && !engine.Device.Contains("内蔵"), "Automatic mode falls back to discrete Vulkan when CUDA is unavailable");
    Check((await engine.Translate(sources[0], "en", "ja", "", ct)).Contains('青'), "Vulkan fallback translates successfully");
} finally { engine.Dispose(); Directory.Move(hidden, cudaPath); }
using (var cancelled = new CancellationTokenSource()) {
    cancelled.Cancel();
    try { await engine.Load(small, "cpu", progress, cancelled.Token); throw new Exception("Cancellation was ignored"); }
    catch (OperationCanceledException) { Check(engine.ProcessId == null, "Cancelled startup leaves no child process"); }
}
await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new { passed, devices = catalog.Devices, benchmarks = rows }, new JsonSerializerOptions { WriteIndented = true }), ct);
Console.WriteLine($"PASS: {passed} checks. Results: {reportPath}");
