using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyPLaMo;

static class GlossaryBackup
{
    public const string Extension = ".myplamo-glossary";
    const int MaxBytes = 128 * 1024 * 1024;
    static readonly byte[] Magic = "MyPLaMoGlossary\n"u8.ToArray();
    sealed record Document(int Version, List<Glossary> Glossaries);

    public static void Write(string path, List<Glossary> glossaries)
    {
        GlossaryStore.Validate(glossaries);
        var json = JsonSerializer.SerializeToUtf8Bytes(new Document(1, glossaries));
        if (json.Length > MaxBytes) throw new IOException("用語集が大きすぎます。バックアップは展開後128MBまでです。");
        // Write beside the destination, then replace only after a complete, flushed write.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write)) {
                file.Write(Magic); file.Write(SHA256.HashData(json));
                using (var compressed = new GZipStream(file, CompressionLevel.Optimal, leaveOpen: true)) compressed.Write(json);
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static List<Glossary> Read(string path)
    {
        try {
            using var file = File.OpenRead(path);
            if (file.Length > MaxBytes) throw new InvalidDataException();
            var header = new byte[Magic.Length]; file.ReadExactly(header);
            if (!header.SequenceEqual(Magic)) throw new InvalidDataException();
            var checksum = new byte[32]; file.ReadExactly(checksum);
            using var compressed = new GZipStream(file, CompressionMode.Decompress);
            using var data = new MemoryStream();
            var buffer = new byte[65536];
            int read;
            while ((read = compressed.Read(buffer)) != 0) {
                if (data.Length + read > MaxBytes) throw new InvalidDataException();
                data.Write(buffer, 0, read);
            }
            var json = data.ToArray();
            if (!CryptographicOperations.FixedTimeEquals(checksum, SHA256.HashData(json))) throw new InvalidDataException();
            var document = JsonSerializer.Deserialize<Document>(json) ?? throw new InvalidDataException();
            if (document.Version != 1) throw new IOException("このバックアップの形式には対応していません。MyPLaMoを更新してください。");
            if (document.Glossaries == null) throw new InvalidDataException();
            GlossaryStore.Validate(document.Glossaries);
            return document.Glossaries;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or JsonException or ArgumentException) {
            throw new IOException("用語集のバックアップを読み込めません。ファイルの形式が違うか、破損しています。", ex);
        }
    }
}
