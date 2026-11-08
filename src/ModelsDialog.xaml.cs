using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using System.Runtime.InteropServices;

namespace MyPLaMo;

public sealed partial class ModelsDialog : ContentDialog
{
    readonly Preferences prefs;
    readonly Engine engine;
    readonly Window owner;
    readonly CancellationToken lifetime;
    CancellationTokenSource? job;
    bool closeWhenIdle;
    public IReadOnlyList<ModelCardViewModel> Cards { get; }

    internal ModelsDialog(Preferences prefs, Engine engine, Window owner, CancellationToken lifetime)
    {
        this.prefs = prefs; this.engine = engine; this.owner = owner; this.lifetime = lifetime;
        Cards = Model.All.Select(x => new ModelCardViewModel(x)).ToArray();
        InitializeComponent();
        Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultContentDialogStyle"];
        var root = ((FrameworkElement)owner.Content).XamlRoot;
        Resources["ContentDialogMaxWidth"] = 940d;
        DialogLayout.Width = Math.Min(830, root.Size.Width - 100);
        BodyScroll.MaxHeight = Math.Max(200, root.Size.Height - 180);
        ConsentCheck.IsChecked = prefs.LicenseAccepted;
        RefreshModels();
    }

    void RefreshModels()
    {
        FolderText.Text = prefs.Folder;
        foreach (var card in Cards) card.Refresh(prefs, ConsentCheck.IsChecked == true, job != null);
        MoveButton.IsEnabled = job == null && Model.All.Any(x => File.Exists(Path.Combine(prefs.Folder, x.file)));
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        MemoryText.Text = GlobalMemoryStatusEx(ref memory) ? $"メモリ {memory.TotalPhysical / 1073741824d:0} GB · 空き {memory.AvailablePhysical / 1073741824d:0.0} GB" : "メモリ情報を取得できませんでした";
    }

    void Consent_Changed(object sender, RoutedEventArgs e) { if (FolderText != null) RefreshModels(); }
    void Refresh_Click(object sender, RoutedEventArgs e) { RefreshModels(); OperationStatus.Text = "保存先のモデル情報を更新しました。"; }
    void Done_Click(object sender, RoutedEventArgs e) { if (job != null) job.Cancel(); else Hide(); }
    void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args) { if (job != null) { args.Cancel = true; job.Cancel(); } }
    internal void RequestClose() { if (job != null) { closeWhenIdle = true; job.Cancel(); } else Hide(); }

    async Task RunOperation(Func<CancellationToken, IProgress<string>, Task> operation)
    {
        if (job != null) return;
        job = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        ChooseFolderButton.IsEnabled = ConsentCheck.IsEnabled = RefreshButton.IsEnabled = false;
        DoneButton.Content = "中断"; OperationProgress.Visibility = Visibility.Visible; RefreshModels();
        try { await operation(job.Token, new Progress<string>(s => OperationStatus.Text = s)); }
        catch (OperationCanceledException) { engine.Dispose(); OperationStatus.Text = "中断しました。同じモデルを選ぶとダウンロードを再開できます。"; }
        catch (Exception ex) { engine.Dispose(); OperationStatus.Text = ex.Message; }
        finally {
            job.Dispose(); job = null;
            ChooseFolderButton.IsEnabled = ConsentCheck.IsEnabled = RefreshButton.IsEnabled = true;
            DoneButton.Content = "完了"; OperationProgress.Visibility = Visibility.Collapsed; RefreshModels();
            if (closeWhenIdle) Hide();
        }
    }

    async void ModelAction_Click(object sender, RoutedEventArgs e)
    {
        if (ConsentCheck.IsChecked != true || job != null) return;
        var model = Model.All.Single(x => x.id == (string)((Button)sender).Tag);
        await RunOperation(async (ct, progress) => {
            await model.Prepare(prefs.Folder, progress, ct);
            await engine.Load(Path.Combine(prefs.Folder, model.file), prefs.GPU, progress, ct);
            OperationStatus.Text = "テスト翻訳中…";
            await engine.Translate("Hello.", "en", "ja", "", ct);
            prefs.ModelId = model.id; prefs.LicenseAccepted = true; prefs.Save();
            OperationStatus.Text = $"{model.id.ToUpperInvariant()} を使用中です。モデルの検証とテスト翻訳が完了しました。";
        });
    }

    async void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (job != null) return;
        try {
            var path = await Ui.PickFolder(owner, prefs.Folder, "モデルの保存先を選択");
            if (path == null) return;
            if (Path.GetFullPath(path).TrimEnd('\\').Equals(Path.GetFullPath(prefs.Folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
            engine.Dispose();
            var previousFolder = prefs.Folder; var previousModel = prefs.ModelId;
            prefs.Folder = path; prefs.ModelId = "";
            try { prefs.Save(); } catch { prefs.Folder = previousFolder; prefs.ModelId = previousModel; throw; }
            RefreshModels();
            OperationStatus.Text = "保存先を変更しました。取得済みモデルは「使用する」から検証して選択できます。";
        } catch (Exception ex) { OperationStatus.Text = ex.Message; }
    }

    async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (job != null) return;
        try {
            var target = await Ui.PickFolder(owner, prefs.Folder, "モデルの移動先を選択");
            if (target == null) return;
            await RunOperation(async (ct, progress) => {
                engine.Dispose();
                var source = prefs.Folder;
                var models = Model.All.Where(x => File.Exists(Path.Combine(source, x.file))).ToArray();
                if (models.Length == 0) throw new IOException("移動するモデルがありません。");
                // All copies must pass verification before switching folders or deleting originals.
                foreach (var model in models) { ct.ThrowIfCancellationRequested(); await ModelMove.CopyVerified(model, source, target, progress, ct); }
                ct.ThrowIfCancellationRequested();
                prefs.Folder = target;
                try { prefs.Save(); } catch { prefs.Folder = source; throw; }
                foreach (var model in models) File.Delete(Path.Combine(source, model.file));
                OperationStatus.Text = $"{models.Length} 件のモデルを検証して移動しました。";
            });
        } catch (Exception ex) { OperationStatus.Text = ex.Message; }
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (job != null) return;
        var button = (Button)sender;
        var model = Model.All.Single(x => x.id == (string)button.Tag);
        var panel = new StackPanel { Spacing = 12, MaxWidth = 320 };
        panel.Children.Add(new TextBlock { Text = $"{model.id.ToUpperInvariant()} のモデルと中断ファイルを削除しますか？", TextWrapping = TextWrapping.Wrap });
        var confirm = new Button { Content = "削除する" };
        AutomationProperties.SetAutomationId(confirm, "ConfirmDeleteModel");
        var flyout = new Flyout { Content = panel };
        confirm.Click += (_, _) => {
            try {
                engine.Dispose(); var path = Path.Combine(prefs.Folder, model.file);
                File.Delete(path); File.Delete(path + ".part");
                if (prefs.ModelId == model.id) { prefs.ModelId = ""; prefs.Save(); }
                OperationStatus.Text = $"{model.id.ToUpperInvariant()} を削除しました。"; RefreshModels();
            } catch (Exception ex) { OperationStatus.Text = ex.Message; }
            flyout.Hide();
        };
        panel.Children.Add(confirm); flyout.ShowAt(button);
    }

    [StructLayout(LayoutKind.Sequential)] struct MemoryStatus {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32")] static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
