using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json;

namespace MyPLaMo;

sealed class Engine : IDisposable
{
    readonly string engineRoot;
    public Engine(string? engineDirectory = null) => engineRoot = engineDirectory ?? ComputeDevices.DefaultRoot;
    public Task<DeviceCatalog> ListDevices(CancellationToken ct) => ComputeDevices.Discover(engineRoot, ct);
    Process? process;
    ChildLifetime? lifetime;
    HttpClient? client;
    string loaded = "";
    public string Device {get; private set;} = "";
    internal int? ProcessId => process is { HasExited: false } ? process.Id : null;
    public void Dispose()
    {
        if(process!=null)
        {
            try { if(!process.HasExited){process.Kill(true);process.WaitForExit(5000);} } catch(InvalidOperationException) {}
            process.Dispose(); process=null;
        }
        lifetime?.Dispose();lifetime=null;
        client?.Dispose(); client=null; loaded="";Device="";
    }
    public Task Load(string path,bool gpu,IProgress<string> progress,CancellationToken ct) => Load(path, gpu ? "auto" : "cpu", progress, ct);
    public async Task Load(string path,string selection,IProgress<string> progress,CancellationToken ct)
    {
        var key = path + "|" + selection;
        if(loaded==key && process is {HasExited:false}) return;
        Dispose();
        var catalog = selection == "cpu" ? new DeviceCatalog([], []) : await ListDevices(ct);
        var candidates = selection == "cpu" ? new[] { ComputeDevice.Cpu } : catalog.Candidates(selection);
        string fallback = selection is not ("auto" or "cpu") && candidates.Length == 1 ? "指定GPUを利用できないため" : "";
        foreach(var device in candidates)
        {
            bool useGpu = device.Backend != "cpu";
            try
            {
                ct.ThrowIfCancellationRequested();
                progress.Report($"モデルを読み込み中（{device.Label}）…" + (fallback == "" ? "" : $" · {fallback}"));
                using var portProbe=new TcpListener(IPAddress.Loopback,0);
                portProbe.Start();
                int port=((IPEndPoint)portProbe.LocalEndpoint).Port;
                portProbe.Stop();
                var token=Guid.NewGuid().ToString("N");
                var info=ComputeDevices.StartInfo(engineRoot, device.Backend);
                foreach(var arg in new[]{"-m",path,"-c","8192","-np","1","--host","127.0.0.1","--port",port.ToString(),"--api-key",token,"--no-ui","--no-agent","--offline","--verbosity","4"})
                    info.ArgumentList.Add(arg);
                info.ArgumentList.Add("--device");
                if(useGpu){
                    // Isolate the selected adapter before the backend initializes; its local index becomes 0.
                    info.Environment[device.Backend == "cuda" ? "CUDA_VISIBLE_DEVICES" : "GGML_VK_VISIBLE_DEVICES"] = device.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    info.ArgumentList.Add(device.Backend == "cuda" ? "CUDA0" : "Vulkan0");
                    info.ArgumentList.Add("--split-mode"); info.ArgumentList.Add("none");
                }
                else {
                    info.ArgumentList.Add("none"); info.ArgumentList.Add("-ngl"); info.ArgumentList.Add("0");
                    info.ArgumentList.Add("--no-op-offload"); info.ArgumentList.Add("--no-warmup");
                }
                bool offload=false;
                process=new Process{StartInfo=info};
                process.ErrorDataReceived+=(_,e)=>{if(e.Data!=null && System.Text.RegularExpressions.Regex.IsMatch(e.Data,@"offloaded [1-9]\d*/")) Volatile.Write(ref offload,true);};
                process.Start();
                lifetime=new ChildLifetime(process.Handle);
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{port}"),Timeout=Timeout.InfiniteTimeSpan};
                client.DefaultRequestHeaders.Authorization=new("Bearer",token);
                var timer=Stopwatch.StartNew();
                while(true)
                {
                    ct.ThrowIfCancellationRequested();
                    if(process.HasExited) throw new IOException($"エンジンの起動に失敗しました（終了コード {process.ExitCode}）。空きメモリとモデルを確認してください。");
                    if(timer.Elapsed.TotalMinutes>6) throw new TimeoutException("モデルの読み込みがタイムアウトしました。");
                    try { using var health=await client.GetAsync("/health",ct); if(health.IsSuccessStatusCode) break; } catch(HttpRequestException){}
                    await Task.Delay(200,ct);
                }
                if(useGpu && !Volatile.Read(ref offload)) throw new IOException("選択したGPUにモデルを配置できませんでした。");
                Device=useGpu ? device.Label : "CPU";
                if(fallback != "") Device += $"（{fallback}）";
                loaded=key;return;
            }
            catch(Exception) when(useGpu && !ct.IsCancellationRequested){
                Dispose(); fallback = selection == "auto" ? "GPUの起動に失敗・代替で実行" : "指定GPUの起動に失敗したため";
                progress.Report($"{device.Label}を利用できませんでした。次の実行先を確認しています…");
            }
            catch{Dispose();throw;}
        }
    }
    public async Task<string> Translate(string source,string from,string to,string style,CancellationToken ct)
    {
        if(source.Contains("<|plamo:",StringComparison.Ordinal)) throw new ArgumentException("モデルの制御記号を含む文章は翻訳できません。");
        string Attr(string name,string value)=>value=="" ? "" : $"<|plamo:key|>{name}<|plamo:val|>{value}";
        var prompt="<|plamo:tag|>dataset<|plamo:msg|>translation<|plamo:tag|>input"+Attr("lang",from)+"<|plamo:msg|>"+source+
            "<|plamo:tag|>output"+Attr("lang",to)+Attr("form",to=="ja"?style:"")+"<|plamo:msg|>";
        using var response=await client!.PostAsJsonAsync("/completion",new {prompt,temperature=0,n_predict=4096,stop=new[]{"<|plamo:tag|>","<|plamo:bos|>"}},ct);
        if(!response.IsSuccessStatusCode) throw new IOException($"翻訳できませんでした（{(int)response.StatusCode}）。文章を短くして再試行してください。");
        using var doc=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root=doc.RootElement;
        if(root.TryGetProperty("stop_type",out var stop) && stop.GetString()=="limit" || root.TryGetProperty("truncated",out var trunc) && trunc.GetBoolean())
            throw new IOException("翻訳の長さが上限に達しました。文章を分けてください。");
        var result=root.GetProperty("content").GetString()?.Trim();
        return !string.IsNullOrEmpty(result) ? result : throw new IOException("訳文が空です。言語を指定して再試行してください。");
    }
}


