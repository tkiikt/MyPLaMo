using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MyPLaMo;

public sealed partial class GlossariesDialog : ContentDialog
{
    readonly GlossaryStore store;
    sealed record TermRow(GlossaryTerm Term) {
        public override string ToString() => $"{Lang.List.First(l => l.Id == Term.From).Name} → {Lang.List.First(l => l.Id == Term.To).Name}　 {Term.Source} → {Term.Target}";
    }
    Glossary? Current => Books.SelectedItem as Glossary;
    internal GlossariesDialog(GlossaryStore store)
    {
        this.store = store; InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 760d;
        TermFrom.ItemsSource = Lang.List; TermTo.ItemsSource = Lang.List;
        TermFrom.SelectedItem = Lang.List.First(l => l.Id == "en"); TermTo.SelectedItem = Lang.List[0];
        RefreshBooks(store.Items.FirstOrDefault());
        Opened += (_, _) => EditorRoot.Height = Math.Min(560, Math.Max(380, XamlRoot.Size.Height - 300));
    }
    void RefreshBooks(Glossary? selected)
    {
        Books.ItemsSource = store.Items.ToArray(); Books.SelectedItem = selected;
        Book_Changed(Books, null!);
    }
    void Book_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BookName == null) return;
        BookName.Text = Current?.Name ?? ""; DeleteBook.IsEnabled = Current != null;
        DeleteBookPrompt.Text = $"用語集「{Current?.Name}」と用語 {Current?.Terms.Count ?? 0} 件を削除しますか？";
        RefreshTerms();
    }
    void RefreshTerms()
    {
        if (Terms == null) return;
        var query = Search.Text.Trim();
        Terms.ItemsSource = (Current?.Terms ?? []).Where(t => t.Source.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Target.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(t => new TermRow(t)).ToArray();
        DeleteTerm.IsEnabled = false;
    }
    void Search_Changed(object sender, TextChangedEventArgs e) => RefreshTerms();
    void Term_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DeleteTerm == null) return;
        DeleteTerm.IsEnabled = Terms.SelectedItem is TermRow;
        if (Terms.SelectedItem is not TermRow row) return;
        TermSource.Text = row.Term.Source; TermTarget.Text = row.Term.Target;
        TermFrom.SelectedItem = Lang.List.First(l => l.Id == row.Term.From); TermTo.SelectedItem = Lang.List.First(l => l.Id == row.Term.To);
    }
    bool Attempt(Action action)
    {
        var snapshot = System.Text.Json.JsonSerializer.Serialize(store.Items);
        try { action(); store.Save(); Message.Text = "保存しました。"; RefreshBooks(Current); return true; }
        catch (Exception ex) {
            var id = Current?.Id;
            store.Items.Clear(); store.Items.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<Glossary>>(snapshot)!);
            RefreshBooks(store.Items.FirstOrDefault(g => g.Id == id)); Message.Text = ex.Message; return false;
        }
    }
    void New_Click(object sender, RoutedEventArgs e) => Attempt(() => {
        var glossary = new Glossary { Name = $"用語集 {store.Items.Count + 1}" }; store.Items.Add(glossary); RefreshBooks(glossary);
    });
    void Rename_Click(object sender, RoutedEventArgs e) => Attempt(() => { if (Current == null) throw new ArgumentException("用語集を作成してください。"); Current.Name = BookName.Text.Trim(); });
    void DeleteBook_Click(object sender, RoutedEventArgs e)
    {
        DeleteBook.Flyout.Hide(); Attempt(() => { if (Current != null) store.Items.Remove(Current); RefreshBooks(store.Items.FirstOrDefault()); });
    }
    void NewTerm_Click(object sender, RoutedEventArgs e) { Terms.SelectedItem = null; TermSource.Text = TermTarget.Text = ""; TermSource.Focus(FocusState.Programmatic); }
    void SaveTerm_Click(object sender, RoutedEventArgs e) { if (Attempt(() => {
        if (Current == null) throw new ArgumentException("先に用語集を作成してください。");
        var term = new GlossaryTerm(TermSource.Text.Trim(), TermTarget.Text.Trim(), ((Lang)TermFrom.SelectedItem).Id, ((Lang)TermTo.SelectedItem).Id);
        if (Terms.SelectedItem is TermRow row) Current.Terms[Current.Terms.IndexOf(row.Term)] = term;
        else Current.Terms.Add(term);
    })) NewTerm_Click(sender, e); }
    void DeleteTerm_Click(object sender, RoutedEventArgs e) { DeleteTerm.Flyout.Hide(); Attempt(() => { if (Terms.SelectedItem is TermRow row) Current?.Terms.Remove(row.Term); }); }
}
