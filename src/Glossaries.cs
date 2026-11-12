using System.Text.Json;

namespace MyPLaMo;

record GlossaryTerm(string Source, string Target, string From = "en", string To = "ja");

sealed class Glossary
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新しい用語集";
    public List<GlossaryTerm> Terms { get; set; } = [];
    public override string ToString() => $"{Name}（{Terms.Count:N0}語）";
}

sealed class GlossaryStore
{
    readonly string path;
    public List<Glossary> Items { get; private set; } = [];
    public string Revision { get; private set; } = "";
    public GlossaryStore(string? directory = null)
    {
        path = Path.Combine(directory ?? Preferences.Home, "glossaries.json");
        if (!File.Exists(path)) return;
        // A broken file must never be silently replaced with an empty collection.
        Items = JsonSerializer.Deserialize<List<Glossary>>(File.ReadAllText(path)) ?? throw new IOException("用語集を読み込めませんでした。");
        Validate();
        Revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
    }
    public void Save()
    {
        Validate();
        var json = JsonSerializer.SerializeToUtf8Bytes(Items, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
        Revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(json));
    }
    void Validate()
    {
        if (Items.Count > 100 || Items.Select(g => g.Id).Distinct().Count() != Items.Count)
            throw new IOException("用語集は100件までです。IDの重複は使用できません。");
        foreach (var g in Items) {
            if (string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.Name) || g.Name.Length > 80 || g.Terms == null || g.Terms.Count > 5000)
                throw new IOException("用語集名は80文字、用語は各5,000件までです。");
            foreach (var t in g.Terms) {
                if (t == null || string.IsNullOrWhiteSpace(t.Source) || string.IsNullOrWhiteSpace(t.Target) || t.Source.Length > 200 || t.Target.Length > 200 ||
                    !Lang.List.Any(l => l.Id == t.From) || !Lang.List.Any(l => l.Id == t.To) || t.From == t.To)
                    throw new IOException("用語の原語・訳語（各200文字まで）と異なる翻訳言語を指定してください。");
                TranslationPrompt.CheckText(t.Source); TranslationPrompt.CheckText(t.Target);
            }
            if (g.Terms.Select(t => (t.From, t.To, t.Source.ToUpperInvariant())).Distinct().Count() != g.Terms.Count)
                throw new IOException("同じ翻訳方向の原語が重複しています。");
        }
    }
    public IReadOnlyList<GlossaryTerm> Resolve(string id)
    {
        if (id == "") return [];
        return Items.FirstOrDefault(g => g.Id == id)?.Terms.ToArray() ?? throw new IOException("選択した用語集が見つかりません。用語集を選び直してください。");
    }
}
