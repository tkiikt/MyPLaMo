using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace MyPLaMo;

record Model(string id,string file,long bytes,string sha,string url,string[]? legacyShas = null)
{
    public static Model[] All { get; } = JsonSerializer.Deserialize<Model[]>(typeof(Model).Assembly.GetManifestResourceStream("MyPLaMo.catalog.json")!)!;
    public override string ToString() => $"{id.ToUpperInvariant()}　·　{bytes / 1e9:0.00} GB";
    public bool AcceptsHash(string hash) => hash.Equals(sha,StringComparison.OrdinalIgnoreCase) || (legacyShas?.Contains(hash,StringComparer.OrdinalIgnoreCase) ?? false);
    public async Task Prepare(string folder, IProgress<string> progress, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(folder)) throw new IOException("保存先を絶対パスで指定してください。");
        Directory.CreateDirectory(folder);
        var dest = Path.Combine(folder,file);
        var part = dest + ".part";
        var existing = File.Exists(dest);
        if (!existing)
        {
            await using var output = new FileStream(part,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,131072,true);
            if (output.Length > bytes) output.SetLength(0);
            var offset = output.Length;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!);
            if (drive.AvailableFreeSpace < bytes-offset) throw new IOException("保存先の空き容量が足りません。");
            if (offset < bytes)
            {
                using var client = new HttpClient {Timeout = Timeout.InfiniteTimeSpan};
                using var req = new HttpRequestMessage(HttpMethod.Get,url);
                if(offset>0) req.Headers.Range = new RangeHeaderValue(offset,null);
                using var response = await client.SendAsync(req,HttpCompletionOption.ResponseHeadersRead,ct);
                response.EnsureSuccessStatusCode();
                if(response.StatusCode == HttpStatusCode.PartialContent)
                {
                    if(response.Content.Headers.ContentRange?.From != offset) throw new IOException("再開位置が一致しません。");
                }
                else { output.SetLength(0); offset = 0; }
                output.Position = offset;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[131072];
                var timer = Stopwatch.StartNew();
                long tick = -500;
                int n;
                while((n=await input.ReadAsync(buffer,ct))>0)
                {
                    if(output.Position+n>bytes) throw new IOException("配布元のファイルサイズが一致しません。");
                    await output.WriteAsync(buffer.AsMemory(0,n),ct);
                    if(timer.ElapsedMilliseconds-tick<250) continue;
                    tick=timer.ElapsedMilliseconds;
                    progress.Report($"取得中　{100.0*output.Position/bytes:0.0}%　残り {(bytes-output.Position)/1e6:0} MB　{(output.Position-offset)/1e6/Math.Max(0.01,timer.Elapsed.TotalSeconds):0.0} MB/s");
                }
                if(output.Length!=bytes) throw new IOException("取得が中断されました。もう一度実行すると再開します。");
            }
        }
        progress.Report("モデルを検証しています…");
        var path = existing ? dest : part;
        string hash;
        await using(var stream=File.OpenRead(path)) hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));
        if(!AcceptsHash(hash))
        {
            if(!existing) File.Delete(part);
            throw new IOException(existing ? "既存ファイルの検証に失敗しました。別の保存先を選ぶか、破損ファイルを移動してください。" : "検証に失敗しました。再取得してください。");
        }
        if(!existing) File.Move(part,dest);
    }
}

sealed class Preferences
{
    public static readonly string Home = Environment.GetEnvironmentVariable("MYPLAMO_PROFILE") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"MyPLaMo");
    static string FileName => Path.Combine(Home,"settings.json");
    public string Folder {get;set;} = Path.Combine(Home,"models");
    public string ModelId {get;set;} = "";
    public string Target {get;set;} = "ja";
    public string Source {get;set;} = "";
    public string Style {get;set;} = "";
    public bool ChromeEnabled {get;set;}
    public bool GPU {get;set;} = true;
    public bool History {get;set;}
    public string Shortcut {get;set;} = "T";
    public bool LicenseAccepted {get;set;}
    public int Limit {get;set;} = 4000;
    public bool Dark {get;set;}
    [System.Text.Json.Serialization.JsonIgnore]
    public Model? Active => Model.All.FirstOrDefault(x=>x.id==ModelId && File.Exists(Path.Combine(Folder,x.file)));
    public static Preferences Load()
    {
        try{return JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FileName)) ?? new();}
        catch(Exception e) when(e is IOException or JsonException){return new();}
    }
    public void Save()
    {
        Directory.CreateDirectory(Home);
        File.WriteAllText(FileName+".tmp",JsonSerializer.Serialize(this));
        File.Move(FileName+".tmp",FileName,true);
    }
}

