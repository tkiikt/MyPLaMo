using MyPLaMo;
using System.Security.Cryptography;

var root = Path.Combine(Path.GetTempPath(), "MyPLaMo-storage-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
var progress = new Progress<string>();
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
async Task Reject(Func<Task> action) { try { await action(); } catch (IOException) { passed++; return; } throw new Exception("Expected verification failure"); }
try {
    byte[] current = [1, 2, 3, 4], legacy = [5, 6, 7], corrupt = [9, 9, 9, 9];
    var model = new Model("test", "model.gguf", current.Length, Hash(current), "https://example.invalid/unused", [Hash(legacy)]);
    var source = Path.Combine(root, "source"); var target = Path.Combine(root, "target");
    Directory.CreateDirectory(source); Directory.CreateDirectory(target);
    var path = Path.Combine(source, model.file);
    await File.WriteAllBytesAsync(path, current);
    await model.Prepare(source, progress, default); Check(File.Exists(path), "Current model rejected");
    await File.WriteAllBytesAsync(path, legacy);
    await model.Prepare(source, progress, default); Check(File.Exists(path), "Legacy model rejected");
    await ModelMove.CopyVerified(model, source, target, progress, default);
    Check((await File.ReadAllBytesAsync(Path.Combine(target, model.file))).SequenceEqual(legacy) && File.Exists(path), "Legacy copy failed or removed source");
    await Reject(() => ModelMove.CopyVerified(model, source, source, progress, default));
    await File.WriteAllBytesAsync(Path.Combine(target, model.file), corrupt);
    await Reject(() => ModelMove.CopyVerified(model, source, target, progress, default));
    Check((await File.ReadAllBytesAsync(path)).SequenceEqual(legacy), "Invalid target damaged source");
    await File.WriteAllBytesAsync(path, corrupt);
    await Reject(() => model.Prepare(source, progress, default));
    Check((await File.ReadAllBytesAsync(path)).SequenceEqual(corrupt), "Verification deleted an existing file");
    Check(Model.All.Length == 4 && Model.All.All(x => x.legacyShas?.Length == 1 && x.AcceptsHash(x.sha) && x.AcceptsHash(x.legacyShas[0]) && !x.AcceptsHash(new string('0', 64))), "Catalog compatibility is incomplete");
    Console.WriteLine($"PASS: {passed} storage and legacy-hash checks");
} finally {
    var resolved = Path.GetFullPath(root);
    if (!string.Equals(Path.GetDirectoryName(resolved)?.TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid test cleanup path");
    Directory.Delete(resolved, true);
}
