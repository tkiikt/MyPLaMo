using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace MyPLaMo;

sealed class ChromeBridge(Func<TranslationRequest, CancellationToken, Task<string>> translate, Func<CancellationToken, Task<object>> metadata, int port = 47832) : IAsyncDisposable
{
    const string Origin = "chrome-extension://oefekpcnnmijlldkpeplijencgdnakbb";
    readonly CancellationTokenSource lifetime = new();
    WebApplication? app;

    public async Task Start()
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        app = builder.Build();
        app.UseWebSockets();
        app.MapGet("/translate", Handle);
        await app.StartAsync(lifetime.Token);
    }

    async Task Handle(HttpContext context)
    {
        if (context.Request.Headers.Origin != Origin || !context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 403;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token);
        request.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = request.Token;
        var buffer = new byte[65536];
        int count = 0;
        WebSocketReceiveResult part;
        do {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), ct);
            if (part.MessageType != WebSocketMessageType.Text) return;
            count += part.Count;
            if (count == buffer.Length) return;
        } while (!part.EndOfMessage);

        object response;
        try {
            using var data = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (data.RootElement.TryGetProperty("protocolVersion", out var protocol) &&
                (!protocol.TryGetInt32(out var version) || version != 1))
                throw new ArgumentException("本体と拡張機能の通信バージョンが対応していません。両方を更新してください。");
            if (data.RootElement.TryGetProperty("action", out var action) && action.GetString() == "metadata") {
                await Send(socket, await metadata(ct), ct);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                return;
            }
            var source = data.RootElement.GetProperty("text").GetString();
            if (string.IsNullOrWhiteSpace(source) || source.Length > 2000)
                throw new ArgumentException("文章を短くしてください。");
            string Field(string name, string fallback = "") => data.RootElement.TryGetProperty(name, out var field) ? field.GetString() ?? fallback : fallback;
            var contextText = Field("context");
            if (contextText.Length > 1200) throw new ArgumentException("参照する文脈が長すぎます。");
            var work = translate(new(source, Field("from"), Field("to", "ja"), Field("style", "polite"), Field("glossaryId"), contextText), ct);
            // Keep Chrome's service worker awake during long model loads.
            while (await Task.WhenAny(work, Task.Delay(15000, ct)) != work) {
                ct.ThrowIfCancellationRequested();
                await Send(socket, new { working = true }, ct);
            }
            response = new { translation = await work };
        } catch (Exception ex) when (!ct.IsCancellationRequested) { response = new { error = ex.Message }; }
        await Send(socket, response, ct);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
    }

    static Task Send(WebSocket socket, object message, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(WithServerInfo(message))), WebSocketMessageType.Text, true, ct);

    static Dictionary<string, object?> WithServerInfo(object message)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(message))!;
        var version = typeof(ChromeBridge).Assembly.GetName().Version;
        fields["server"] = new { platform = "windows", appVersion = $"{version?.Major ?? 0}.{version?.Minor ?? 0}.{version?.Build ?? 0}", protocolVersion = 1, capabilities = new[] { "options", "glossaries", "context" } };
        return fields;
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        if (app != null) {
            await app.StopAsync();
            await app.DisposeAsync();
        }
        lifetime.Dispose();
    }
}
