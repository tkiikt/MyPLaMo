using MyPLaMo;
using System.Text.Json;
using System.Net.WebSockets;
using System.Text;

var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
async Task Reject(Func<Task> run, string name) { try { await run(); } catch { Check(true,name); return; } throw new Exception("Expected rejection: " + name); }
var calls = new List<(string Source, string Context, TranslationPair[] History)>();
Task<string> Fake(string text,string context,IReadOnlyList<TranslationPair> history,CancellationToken token) { token.ThrowIfCancellationRequested(); calls.Add((text,context,history.ToArray())); return Task.FromResult("訳：" + text); }
Task<string> Translate(string text,string format) => DocumentTranslation.Translate(new(text,Format:format),Fake,default);

var terms = new[] { new GlossaryTerm("bank","銀行"), new GlossaryTerm("bank","土手","en","fr"), new GlossaryTerm("cat","猫") };
var prompt = TranslationPrompt.Build("The bank is closed.","en","ja","plain","Finance",terms,[new("Hello","こんにちは")]);
Check(prompt.Contains("context<|plamo:msg|>Finance") && prompt.Contains("glossary<|plamo:index|>bank<|plamo:item|>銀行") && !prompt.Contains("土手") && !prompt.Contains("猫"),"native context and matching glossary direction");
Check(prompt.IndexOf("Finance") < prompt.IndexOf("Hello") && prompt.IndexOf("こんにちは") < prompt.IndexOf("glossary") && prompt.EndsWith("form<|plamo:val|>plain<|plamo:msg|>"),"PFML3 context/history/glossary order and style");
await Reject(() => Task.FromResult(TranslationPrompt.Build("<|plamo:tag|>","","ja","")),"reject source control tokens");
await Reject(() => Task.FromResult(TranslationPrompt.Build("Hello","","ja","","<|plamo:msg|>")),"reject context control tokens");
await Reject(() => Task.FromResult(TranslationPrompt.Build("Hello","invalid","ja","")),"reject unsupported source language");
await Reject(() => Task.FromResult(TranslationPrompt.Build(string.Join(' ',Enumerable.Range(0,33).Select(i => "term"+i)),"en","ja","",glossary:Enumerable.Range(0,33).Select(i => new GlossaryTerm("term"+i,"訳"+i)).ToArray())),"glossary prompt budget does not silently drop terms");

var json = "{\r\n  \"title\": \"Hello world\", \"nested\": [\"Second text\", true, 42],\r\n  \"日本語キー\": \"A quote: \\\"hello\\\"\", \"url\": \"https://example.com/test\"\r\n}";
var result = await Translate(json,"json");
using (var doc = JsonDocument.Parse(result)) {
    Check(doc.RootElement.GetProperty("title").GetString() == "訳：Hello world", "JSON values translated");
    Check(doc.RootElement.GetProperty("nested")[1].GetBoolean() && doc.RootElement.GetProperty("nested")[2].GetInt32() == 42 && doc.RootElement.GetProperty("url").GetString() == "https://example.com/test", "JSON keys, types and URL preserved");
}
Check(result.StartsWith("{\r\n  \"title\": ") && result.EndsWith("\r\n}"),"JSON original indentation and CRLF retained");
await Reject(() => Translate("{broken}","json"),"invalid JSON fails without guessing");
calls.Clear();
var markdown = "# Hello world\r\n\r\nA **bold word** with `inline code` and [a link](https://example.com).\r\n\r\n| Column | Value |\r\n| --- | --- |\r\n| First | Second |\r\n\r\n```json\r\n{\"code\":\"keep me\"}\r\n```\r\n";
result = await Translate(markdown,"markdown");
Check(result.Contains("# 訳：Hello world\r\n") && result.Contains("**訳：bold word**") && result.Contains("[訳：a link](https://example.com)"),"Markdown heading, emphasis and link syntax preserved");
Check(result.Contains("`inline code`") && result.Contains("```json\r\n{\"code\":\"keep me\"}\r\n```") && !calls.Any(c => c.Source.Contains("inline code") || c.Source.Contains("keep me")),"Markdown code never sent for translation");
Check(result.Contains("| --- | --- |") && result.Contains("| 訳：First | 訳：Second |"),"Markdown table layout preserved");
result = await Translate("---\ntitle: Original title\n---\n# Heading\n\nText\n","auto");
Check(result.StartsWith("---\ntitle: Original title\n---\n# 訳：Heading"),"Markdown YAML front matter is preserved");

calls.Clear();
var yaml = "# keep comment\ntitle: Hello world # inline comment\nenabled: true\ncount: 42\nnested:\n  - name: 'First text'\n    url: https://example.com/x\nflow: {title: \"Other text\", enabled: false}\nref: &ref Hello anchor\nalias: *ref\n";
result = await Translate(yaml,"yaml");
Check(result.Contains("title: 訳：Hello world # inline comment") && result.Contains("name: '訳：First text'") && result.Contains("title: \"訳：Other text\""),"YAML nested and flow values translated");
Check(result.Contains("# keep comment") && result.Contains("enabled: true") && result.Contains("count: 42") && result.Contains("url: https://example.com/x") && result.Contains("ref: &ref Hello anchor\nalias: *ref"),"YAML keys, comments, types, URL, anchors and aliases retained");
result = await Translate("message: |\n  First line\n  Second line\nnext: Other text\n","yaml");
Check(result.Contains("message: |\n  訳：First line\n  訳：Second line\nnext:"),"YAML literal block and indentation retained");
result = await Translate("message: >-\n  First line\n  Second line\n\nnext: Other text\n","yaml");
Check(result.Contains("message: >-\n  訳：First line\n  訳：Second line\n"),"YAML folded block retains physical line layout");
result = await Translate("exponent: 1e5\nhex: 0xFF\ndate: 2026-11-12T12:30:00Z\nmessage: Hello\n","yaml");
Check(result.Contains("exponent: 1e5\nhex: 0xFF\ndate: 2026-11-12T12:30:00Z\n"),"YAML numeric and timestamp scalar types retained");
result = await DocumentTranslation.Translate(new("message: Hello",Format:"yaml"),(_,_,_,_) => Task.FromResult("true"),default);
Check(result.Contains("\"true\""),"YAML translated string cannot become boolean");
result = await DocumentTranslation.Translate(new("message: Hello",Format:"yaml"),(_,_,_,_) => Task.FromResult("123"),default);
Check(result.Contains("\"123\""),"YAML translated string cannot become number");
await Reject(() => Translate("title: [broken","yaml"),"invalid YAML rejected");

calls.Clear();
result = await Translate("First paragraph.\n\nSecond paragraph.","text");
Check(result == "訳：First paragraph.\n\n訳：Second paragraph." && calls[1].History[0].Source == "First paragraph.","paragraphs preserve spacing and use recent context");
calls.Clear();
await DocumentTranslation.Translate(new("First.\nSecond.",UseContext:false),Fake,default);
Check(calls.All(c => c.Context == "" && c.History.Length == 0),"context can be disabled");
Check(DocumentTranslation.Detect("{\"a\":\"hello\"}") == "json" && DocumentTranslation.Detect("# Heading\n\nText") == "markdown" && DocumentTranslation.Detect("title: Hello") == "yaml", "document auto detection");
using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); await Reject(() => DocumentTranslation.Translate(new("hello"),Fake,cancelled.Token),"cancellation honored"); }

var directory = Path.Combine(Path.GetTempPath(),"MyPLaMo-glossary-"+Guid.NewGuid().ToString("N"));
try {
    var store = new GlossaryStore(directory); var book = new Glossary {Name="Finance",Terms=[terms[0]]}; store.Items.Add(book); store.Save();
    var reloaded = new GlossaryStore(directory);
    Check(reloaded.Resolve(book.Id).Single().Target == "銀行" && reloaded.Revision == store.Revision,"glossary atomic persistence and revision");
    book.Terms.Add(terms[0]); await Reject(() => Task.Run(store.Save),"duplicate glossary source rejected"); book.Terms.RemoveAt(1);
    await Reject(() => Task.FromResult(store.Resolve("missing")),"missing glossary is explicit error");
    File.WriteAllText(Path.Combine(directory,"glossaries.json"),"broken"); await Reject(() => Task.FromResult(new GlossaryStore(directory)),"corrupt glossary not overwritten");
    Check(File.ReadAllText(Path.Combine(directory,"glossaries.json")) == "broken","corrupt glossary file retained");
} finally { if (Directory.Exists(directory)) Directory.Delete(directory,true); }

if (args.Contains("--bridge")) {
    TranslationRequest? received = null;
    using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0); probe.Start(); var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
    await using var bridge = new ChromeBridge((r,_) => { received = r; return Task.FromResult("こんにちは"); },_ => Task.FromResult<object>(new {glossaries=Array.Empty<object>(),modelId="test",glossaryRevision="test"}),port);
    await bridge.Start();
    async Task<JsonElement> Request(object message,string origin="chrome-extension://oefekpcnnmijlldkpeplijencgdnakbb") {
        using var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Origin",origin);
        await socket.ConnectAsync(new($"ws://127.0.0.1:{port}/translate"),default);
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message),WebSocketMessageType.Text,true,default);
        var bytes = new byte[65536]; var part = await socket.ReceiveAsync(bytes,default);
        return JsonDocument.Parse(bytes.AsMemory(0,part.Count)).RootElement.Clone();
    }
    var info = await Request(new {action="metadata",protocolVersion=1});
    Check(info.GetProperty("server").GetProperty("capabilities").EnumerateArray().Any(x => x.GetString() == "glossaries"),"bridge advertises options and glossary capabilities");
    var reply = await Request(new {text="Hello",from="en",to="ja",style="plain",glossaryId="id",context="Finance",protocolVersion=1});
    Check(reply.GetProperty("translation").GetString() == "こんにちは" && received is {From:"en",Style:"plain",GlossaryId:"id",Context:"Finance"},"bridge carries all translation options");
    await Reject(() => Request(new {text="Hello"},"https://example.com"),"bridge rejects unauthorized web origin");
}
if (args.Contains("--model")) {
    using var engine = new Engine(Path.GetFullPath("engine"));
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    await engine.Load(Path.GetFullPath("models/plamo-3-translate-2b-Q4_K_M.gguf"),"auto",new Progress<string>(Console.WriteLine),timeout.Token);
    var actual = await engine.Translate("The cat is sleeping.","en","ja","polite",timeout.Token,glossary:[new("cat","ネコちゃん")]);
    Check(actual.Contains("ネコちゃん"),"real 2B inference uses glossary: " + actual);
    actual = await engine.Translate("プロジェクトの管理画面を開いてください。","ja","en","",timeout.Token,glossary:[new("管理画面","dashboard","ja","en"),new("プロジェクト","project","ja","en")]);
    Check(actual.Contains("dashboard"),"real 2B Japanese to English glossary: " + actual);
    actual = await engine.Translate("かみを切りました。","ja","en","",timeout.Token,"工作で折り紙を作っています。");
    Check(actual.Contains("paper"),"real 2B context disambiguation: " + actual);
    var structured = await DocumentTranslation.Translate(new("{\n  \"message\": \"The cat is sleeping.\",\n  \"count\": 3\n}",From:"en",To:"ja",Format:"json"),(text,context,history,ct) => engine.Translate(text,"en","ja","plain",ct,context,[new("cat","ネコちゃん")],history),timeout.Token);
    using var doc = JsonDocument.Parse(structured); Check(doc.RootElement.GetProperty("count").GetInt32() == 3 && doc.RootElement.GetProperty("message").GetString()!.Contains("ネコちゃん"),"real inference preserves JSON and glossary: " + structured);
}
Console.WriteLine($"{count} checks passed");
