using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace MyPLaMo;

record TranslationPair(string Source, string Target);
record TranslationRequest(string Text, string From = "", string To = "ja", string Style = "", string GlossaryId = "", string Context = "", string Format = "text", bool UseContext = true);

static class TranslationPrompt
{
    public static void CheckText(string text)
    {
        if (text.Contains("<|plamo:", StringComparison.Ordinal)) throw new ArgumentException("モデルの制御記号を含む文章は翻訳できません。");
    }
    public static string Build(string source, string from, string to, string style, string context = "", IReadOnlyList<GlossaryTerm>? glossary = null, IReadOnlyList<TranslationPair>? history = null)
    {
        CheckText(source); CheckText(context);
        if (!Lang.List.Any(l => l.Id == to) || (from != "" && !Lang.List.Any(l => l.Id == from)) || style is not ("" or "plain" or "polite"))
            throw new ArgumentException("翻訳言語または文体が正しくありません。");
        if (from == to) throw new ArgumentException("翻訳元と翻訳先に異なる言語を選んでください。");
        static string Attr(string name, string value) => value == "" ? "" : $"<|plamo:key|>{name}<|plamo:val|>{value}";
        var prompt = new StringBuilder("<|plamo:tag|>dataset<|plamo:msg|>translation");
        if (context.Length > 0) prompt.Append("<|plamo:tag|>context<|plamo:msg|>").Append(context[..Math.Min(1200, context.Length)]);
        foreach (var pair in history ?? []) {
            CheckText(pair.Source); CheckText(pair.Target);
            prompt.Append("<|plamo:tag|>input").Append(Attr("lang", from)).Append("<|plamo:msg|>").Append(pair.Source)
                .Append("<|plamo:tag|>output").Append(Attr("lang", to)).Append("<|plamo:msg|>").Append(pair.Target);
        }
        var terms = (glossary ?? []).Where(t => t.To == to && (from == "" || t.From == from) && source.Contains(t.Source, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (terms.Length > 32 || terms.Sum(t => t.Source.Length + t.Target.Length) > 2000)
            throw new ArgumentException("この文章に一致する用語が多すぎます。用語集または原文を分けてください（32語・合計2,000文字まで）。");
        if (terms.Length > 0) {
            prompt.Append("<|plamo:tag|>glossary");
            foreach (var term in terms) {
                CheckText(term.Source); CheckText(term.Target);
                prompt.Append("<|plamo:index|>").Append(term.Source).Append("<|plamo:item|>").Append(term.Target);
            }
        }
        return prompt.Append("<|plamo:tag|>input").Append(Attr("lang", from)).Append("<|plamo:msg|>").Append(source)
            .Append("<|plamo:tag|>output").Append(Attr("lang", to)).Append(Attr("form", to == "ja" ? style : "")).Append("<|plamo:msg|>").ToString();
    }
}

// Only natural-language spans are sent to the model. Syntax is copied from the original.
static class DocumentTranslation
{
    record Span(int Start, int Length, string Text, Func<string, string> Encode);
    static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static string Quote(string text) => JsonSerializer.Serialize(text, JsonOptions);
    static bool HasText(string text) => Regex.IsMatch(text, @"\p{L}") && !Regex.IsMatch(text, @"^(?:[a-z][a-z0-9+.-]*://|/|\$\{|[A-Z]:\\|\S+@\S+\.\S+)", RegexOptions.IgnoreCase);
    public static string Detect(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('[')) { try { using var doc = JsonDocument.Parse(text); return "json"; } catch (JsonException) { } }
        if (Regex.IsMatch(text, @"(?m)^(?:#{1,6} |```|~~~|\s*[-*+] |\|)|\[[^\]]+\]\(|\*\*|`[^`]+`")) return "markdown";
        if (Regex.IsMatch(text, @"(?m)^\s*[\w.-]+:\s+\S")) return "yaml";
        return "text";
    }
    public static async Task<string> Translate(TranslationRequest request, Func<string, string, IReadOnlyList<TranslationPair>, CancellationToken, Task<string>> translate, CancellationToken ct)
    {
        TranslationPrompt.CheckText(request.Text); TranslationPrompt.CheckText(request.Context);
        var format = request.Format == "auto" ? Detect(request.Text) : request.Format;
        var spans = format switch {
            "text" => new List<Span> { new(0, request.Text.Length, request.Text, s => s) },
            "json" => JsonSpans(request.Text), "yaml" => YamlSpans(request.Text), "markdown" => MarkdownSpans(request.Text),
            _ => throw new ArgumentException("文書形式が正しくありません。")
        };
        var result = new StringBuilder(); int position = 0;
        var history = new List<TranslationPair>();
        var context = request.UseContext ? string.Join("\n", new[] { request.Context, format == "text" ? "" : request.Text[..Math.Min(800, request.Text.Length)] }.Where(s => s != "")) : "";
        foreach (var span in spans.OrderBy(s => s.Start)) {
            ct.ThrowIfCancellationRequested();
            result.Append(request.Text.AsSpan(position, span.Start - position));
            var translated = new StringBuilder();
            foreach (var part in Split(span.Text, 1400)) {
                if (!HasText(part)) { translated.Append(part); continue; }
                var core = part.Trim();
                var output = await translate(core, context, request.UseContext ? history : [], ct);
                translated.Append(part.AsSpan(0, part.Length - part.TrimStart().Length)).Append(output.Trim()).Append(part.AsSpan(part.TrimEnd().Length));
                if (request.UseContext) {
                    history.Add(new(core, output));
                    while (history.Count > 2 || history.Sum(p => p.Source.Length + p.Target.Length) > 1600) history.RemoveAt(0);
                }
            }
            result.Append(span.Encode(translated.ToString())); position = span.Start + span.Length;
        }
        result.Append(request.Text.AsSpan(position));
        var value = result.ToString();
        // Reject invalid output rather than delivering a damaged configuration/document.
        if (format == "json") { using var doc = JsonDocument.Parse(value); }
        if (format == "yaml") { var parser = new Parser(new StringReader(value)); while (parser.MoveNext()) { } }
        return value;
    }
    static IEnumerable<string> Split(string text, int limit)
    {
        // Paragraph boundaries supply recent translation history for the next paragraph.
        foreach (Match paragraph in Regex.Matches(text, @"[^\r\n]+|\r\n|[\r\n]")) {
            var part = paragraph.Value;
            while (part.Length > limit) {
                var stop = part.LastIndexOfAny([' ', '。', '.', '!', '?', '！', '？'], limit - 1, limit);
                var size = stop >= limit / 2 ? stop + 1 : char.IsHighSurrogate(part[limit - 1]) ? limit - 1 : limit;
                yield return part[..size]; part = part[size..];
            }
            yield return part;
        }
    }
    static List<Span> JsonSpans(string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source); var reader = new Utf8JsonReader(bytes); var spans = new List<Span>();
        while (reader.Read()) if (reader.TokenType == JsonTokenType.String && HasText(reader.GetString()!)) {
            var start = Encoding.UTF8.GetCharCount(bytes.AsSpan(0, (int)reader.TokenStartIndex));
            var length = Encoding.UTF8.GetCharCount(bytes.AsSpan((int)reader.TokenStartIndex, (int)(reader.BytesConsumed - reader.TokenStartIndex)));
            spans.Add(new(start, length, reader.GetString()!, Quote));
        }
        return spans;
    }
    sealed class Container(bool mapping, bool key, bool suppressed) { public bool Mapping = mapping; public bool Key = key; public bool Suppressed = suppressed; }
    static List<Span> YamlSpans(string source)
    {
        var parser = new Parser(new StringReader(source)); var stack = new Stack<Container>(); var spans = new List<Span>();
        while (parser.MoveNext()) {
            var node = parser.Current;
            bool key = stack.TryPeek(out var parent) && parent.Mapping && parent.Key;
            if (node is MappingStart or SequenceStart) {
                if (parent?.Mapping == true) parent.Key = !parent.Key;
                stack.Push(new(node is MappingStart, node is MappingStart, key || parent?.Suppressed == true));
            } else if (node is MappingEnd or SequenceEnd) stack.Pop();
            else if (node is Scalar scalar) {
                if (!key && parent?.Suppressed != true && HasText(scalar.Value) && !(scalar.Style == ScalarStyle.Plain && Regex.IsMatch(scalar.Value, @"^(?:true|false|null|yes|no|on|off|~|[+\-]?(?:0x[0-9a-f_]+|0o[0-7_]+|[0-9][0-9_.:eE+\-]*|\.(?:inf|nan))|\d{4}-\d{2}-\d{2}(?:[Tt ].*)?)$", RegexOptions.IgnoreCase))) {
                    // Explicit tags/anchors are preserved, but their scalar syntax can be ambiguous: leave them intact.
                    if (scalar.Anchor.IsEmpty && scalar.Tag.IsEmpty) {
                        int start = checked((int)scalar.Start.Index), length = checked((int)scalar.End.Index) - start;
                        var original = source.Substring(start, length);
                        string Encode(string text) {
                            if (scalar.Style == ScalarStyle.SingleQuoted) return "'" + text.Replace("'", "''") + "'";
                            if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded) {
                                var firstNewline = original.IndexOf('\n');
                                if (firstNewline < 0) return Quote(text);
                                var indent = Regex.Match(original[(firstNewline + 1)..], @"^ *").Value;
                                var newline = original.Contains("\r\n") ? "\r\n" : "\n";
                                return original[..(firstNewline + 1)] + string.Join(newline, text.TrimEnd('\r', '\n').Split('\n').Select(l => indent + l.TrimEnd('\r'))) + (original.EndsWith('\n') ? newline : "");
                            }
                            if (scalar.Style == ScalarStyle.DoubleQuoted || Regex.IsMatch(text, @"[:#\[\]{},&*!|>'\x22%@`\r\n]|^[-? ]| $|^(?:true|false|null|yes|no|on|off|~|[+\-]?[0-9][0-9.eE+\-:]*|[+\-]?\.(?:inf|nan))$", RegexOptions.IgnoreCase)) return Quote(text);
                            return text;
                        }
                        var value = scalar.Value;
                        if (scalar.Style is ScalarStyle.Literal or ScalarStyle.Folded) {
                            var firstNewline = original.IndexOf('\n');
                            if (firstNewline >= 0) {
                                var body = original[(firstNewline + 1)..];
                                var indent = Regex.Match(body, @"(?m)^ *(?=\S)").Value;
                                value = string.Join("\n", body.Split('\n').Select(line => line.StartsWith(indent, StringComparison.Ordinal) ? line[indent.Length..] : line));
                            }
                        }
                        spans.Add(new(start, length, value, Encode));
                    }
                }
                if (parent?.Mapping == true) parent.Key = !parent.Key;
            } else if (node is AnchorAlias && parent?.Mapping == true) parent.Key = !parent.Key;
        }
        return spans;
    }
    static List<Span> MarkdownSpans(string source)
    {
        var doc = Markdown.Parse(source, new MarkdownPipelineBuilder().UseAdvancedExtensions().UseYamlFrontMatter().UsePreciseSourceLocation().Build());
        return doc.Descendants<LiteralInline>().Where(l => l.Span.Start >= 0 && l.Span.End < source.Length && HasText(l.Content.ToString()))
            .Select(l => {
                var raw = source.Substring(l.Span.Start, l.Span.Length);
                var text = raw[..(raw.Length - raw.TrimStart().Length)] + l.Content.ToString().Trim() + raw[raw.TrimEnd().Length..];
                return new Span(l.Span.Start, l.Span.Length, text, value => Regex.Replace(value, @"([\\`*_{}\[\]<>#|])", @"\$1"));
            }).ToList();
    }
}
