using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace MyPLaMo;

record Entry(string Source, string Result, string From, string To)
{
    public override string ToString() => Source.ReplaceLineEndings(" ")[..Math.Min(60, Source.ReplaceLineEndings(" ").Length)];
}

public sealed partial class MainWindow : Window
{
    readonly Preferences prefs = Preferences.Load();
    readonly Engine engine = new();
    readonly SemaphoreSlim engineGate = new(1);
    readonly CancellationTokenSource lifetime = new();
    readonly List<Entry> entries = [];
    GlossaryStore? glossaries;
    string glossaryError = "";
    sealed record GlossaryOption(string Id, string Name) { public override string ToString() => Name; }
    static readonly string[] Formats = ["auto", "text", "markdown", "json", "yaml"];
    readonly NativeDesktop desktop;
    readonly bool startHidden;
    readonly nint hwnd;
    ChromeBridge? bridge;
    ContentDialog? dialog;
    CancellationTokenSource? job;
    bool initialized, syncingChrome, syncingOptions, modal, capturing, quitting;
    nint origin;
    uint originPid;
    static string LogPath => Path.Combine(Preferences.Home, "history.json");

    public MainWindow(bool startHidden)
    {
        this.startHidden = startHidden;
        InitializeComponent();
        Root.RequestedTheme = prefs.Dark ? ElementTheme.Dark : ElementTheme.Light;
        Title = "MyPLaMo · ローカル翻訳";
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = NativeDesktop.GetDpiForWindow(hwnd) / 96.0;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1120 * scale), area.Width);
        var height = Math.Min((int)(860 * scale), area.Height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "MyPLaMo.ico"));
        AppWindow.TitleBar.PreferredTheme = prefs.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
        FromLanguage.ItemsSource = new[] { new Lang("", "自動検出") }.Concat(Lang.List).ToArray();
        ToLanguage.ItemsSource = Lang.List;
        SelectLanguage(FromLanguage, prefs.Source); SelectLanguage(ToLanguage, prefs.Target);
        TranslationStyle.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { "", "polite", "plain" }, prefs.Style));
        TranslationStyle.IsEnabled = Language(ToLanguage) == "ja";
        try { glossaries = new GlossaryStore(); } catch (Exception ex) { glossaryError = ex.Message; }
        RefreshGlossaries();
        DocumentFormat.SelectedIndex = Math.Max(0, Array.IndexOf(Formats, prefs.DocumentFormat));
        UseContext.IsChecked = prefs.UseContext;
        desktop = new NativeDesktop(hwnd, Reveal, () => _ = QuitAsync(), () => { if (!modal && !capturing && job == null) _ = CaptureSelection(); });
        var translateKey = new KeyboardAccelerator { Key = VirtualKey.Enter, Modifiers = VirtualKeyModifiers.Control };
        translateKey.Invoked += async (_, e) => { e.Handled = true; if (job == null) await RunTranslation(); };
        Root.KeyboardAccelerators.Add(translateKey);
        // Multiline TextBox handles Enter before bubbling accelerators. Intercept it in the tunnel.
        Root.PreviewKeyDown += async (_, e) => {
            if (e.Key == VirtualKey.Enter && (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) {
                e.Handled = true;
                if (job == null) await RunTranslation();
            } else if (e.Key == VirtualKey.Escape && job != null) { job.Cancel(); e.Handled = true; }
        };
        AppWindow.Closing += (_, e) => { if (!quitting) { e.Cancel = true; AppWindow.Hide(); } };
        Closed += (_, _) => { lifetime.Cancel(); desktop.Dispose(); engine.Dispose(); lifetime.Dispose(); };
        Root.Loaded += Loaded;
        try {
            if (File.Exists(LogPath)) entries.AddRange(JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(LogPath)) ?? []);
        } catch (Exception e) when (e is IOException or JsonException) { }
    }

    async void Loaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return;
        initialized = true;
        RefreshReady();
        ChromeToggle.IsChecked = prefs.ChromeEnabled;
        if (prefs.Active == null) await OpenModels();
        else if (startHidden) AppWindow.Hide();
    }

    static string Language(ComboBox box) => (box.SelectedItem as Lang)?.Id ?? "";
    static void SelectLanguage(ComboBox box, string id) => box.SelectedItem = box.Items.Cast<Lang>().FirstOrDefault(x => x.Id == id) ?? box.Items[0];
    void Reveal()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        Activate(); NativeDesktop.SetForegroundWindow(hwnd); InputText.Focus(FocusState.Programmatic);
    }

    async Task QuitAsync()
    {
        if (quitting) return;
        quitting = true;
        lifetime.Cancel(); job?.Cancel();
        if (dialog is ModelsDialog models) models.RequestClose(); else dialog?.Hide();
        await engineGate.WaitAsync();
        try {
            if (bridge != null) { await bridge.DisposeAsync(); bridge = null; }
            engine.Dispose();
        } finally { engineGate.Release(); Close(); }
    }

    void RefreshReady()
    {
        var model = prefs.Active;
        ModelText.Text = model == null ? "翻訳モデルを選んでください" : $"PLaMo 3 Translate · {model.id.ToUpperInvariant()} · GGUF";
        StatusText.Text = model == null ? "右上の「モデル」からダウンロード、または取得済みモデルを選択できます。" : engine.Device == "" ? "モデル未読み込み · " + prefs.ExecutionLabel : "準備完了 · " + engine.Device;
        CountText.Text = $"{InputText.Text.Length:N0} / {prefs.Limit:N0} 文字";
        if (!desktop.RegisterShortcut(prefs.Shortcut)) StatusText.Text += " · ショートカットが競合しています。設定で変更してください。";
    }

    async void Models_Click(object sender, RoutedEventArgs e) => await OpenModels();
    async Task OpenModels()
    {
        if (modal || job != null || quitting) return;
        modal = true;
        await engineGate.WaitAsync();
        try {
            var models = new ModelsDialog(prefs, engine, this, lifetime.Token) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
            dialog = models;
            await models.ShowAsync();
        } catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { dialog = null; modal = false; engineGate.Release(); RefreshReady(); }
    }

    async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (modal || job != null || quitting) return;
        modal = true;
        await engineGate.WaitAsync();
        try {
            var settings = new SettingsDialog(prefs, engine, lifetime.Token) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
            dialog = settings;
            if (await settings.ShowAsync() == ContentDialogResult.Primary) {
                engine.Dispose();
                Root.RequestedTheme = prefs.Dark ? ElementTheme.Dark : ElementTheme.Light;
                AppWindow.TitleBar.PreferredTheme = prefs.Dark ? TitleBarTheme.Dark : TitleBarTheme.Light;
            }
        } catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { dialog = null; modal = false; engineGate.Release(); RefreshReady(); }
    }

    async void Chrome_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || syncingChrome) return;
        syncingChrome = true; ChromeToggle.IsEnabled = false;
        try {
            if (ChromeToggle.IsChecked == true) {
                bridge = new ChromeBridge((request, ct) => OnUiThread(() => TranslateForChrome(request, ct)), ct => OnUiThread(() => Task.FromResult<object>(new {
                    glossaries = glossaries?.Items.Select(g => new { id = g.Id, name = g.Name, count = g.Terms.Count }).ToArray() ?? [],
                    glossaryRevision = glossaries?.Revision ?? "", glossaryError,
                    modelId = prefs.ModelId,
                    languages = Lang.List.Select(l => new { id = l.Id, name = l.Name }).ToArray()
                })));
                await bridge.Start();
            } else if (bridge != null) { await bridge.DisposeAsync(); bridge = null; }
            prefs.ChromeEnabled = ChromeToggle.IsChecked == true; prefs.Save();
        } catch (Exception ex) {
            if (bridge != null) await bridge.DisposeAsync();
            bridge = null; ChromeToggle.IsChecked = false; prefs.ChromeEnabled = false;
            StatusText.Text = "Chrome連携を開始できませんでした。 " + ex.GetBaseException().Message;
        } finally { syncingChrome = false; ChromeToggle.IsEnabled = true; }
    }

    Task<T> OnUiThread<T>(Func<Task<T>> action)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () => {
            try { result.TrySetResult(await action()); }
            catch (OperationCanceledException) { result.TrySetCanceled(); }
            catch (Exception ex) { result.TrySetException(ex); }
        })) result.TrySetException(new IOException("MyPLaMoを終了しています。"));
        return result.Task;
    }

    async Task<string> TranslateForChrome(TranslationRequest request, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        await engineGate.WaitAsync(linked.Token);
        try {
            var model = prefs.Active;
            if (model == null || !prefs.LicenseAccepted) throw new IOException("MyPLaMoの「モデル」でモデルの準備を完了してください。");
            await engine.Load(Path.Combine(prefs.Folder, model.file), prefs.ExecutionDevice, new Progress<string>(), linked.Token);
            var terms = GetTerms(request.GlossaryId);
            return await DocumentTranslation.Translate(request, (source, context, history, token) => engine.Translate(source, request.From, request.To, request.Style, token, context, terms, history), linked.Token);
        } finally { engineGate.Release(); }
    }

    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || job != null) return;
        OutputText.Text = "";
        TranslationStyle.IsEnabled = Language(ToLanguage) == "ja";
        prefs.Source = Language(FromLanguage); prefs.Target = Language(ToLanguage);
        prefs.Style = new[] { "", "polite", "plain" }[Math.Max(0, TranslationStyle.SelectedIndex)]; prefs.Save();
    }

    void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (CountText == null) return;
        CountText.Text = $"{InputText.Text.Length:N0} / {prefs.Limit:N0} 文字";
        if (job == null && OutputText != null) OutputText.Text = "";
    }

    void Swap_Click(object sender, RoutedEventArgs e)
    {
        var sourceLanguage = Language(FromLanguage);
        if (sourceLanguage == "") sourceLanguage = InputText.Text.Length == 0 ? Language(ToLanguage) == "ja" ? "en" : "ja" : LanguageDetection.Guess(InputText.Text);
        if (sourceLanguage == "") { StatusText.Text = "入れ替えるには原文の言語を選んでください。"; return; }
        var targetLanguage = Language(ToLanguage); var source = InputText.Text; var target = OutputText.Text;
        SelectLanguage(ToLanguage, sourceLanguage); SelectLanguage(FromLanguage, targetLanguage);
        InputText.Text = target; OutputText.Text = source;
    }
    void Clear_Click(object sender, RoutedEventArgs e) { InputText.Text = ""; OutputText.Text = ""; }
    async void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { var data = Clipboard.GetContent(); if (data.Contains(StandardDataFormats.Text)) InputText.Text = await data.GetTextAsync(); }
        catch (Exception ex) { StatusText.Text = "貼り付けできませんでした。 " + ex.Message; }
    }
    void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { if (OutputText.Text.Length > 0) { Ui.CopyText(OutputText.Text); StatusText.Text = "訳文をコピーしました。"; } }
        catch (Exception ex) { StatusText.Text = "コピーできませんでした。 " + ex.Message; }
    }
    async void Translate_Click(object sender, RoutedEventArgs e) { if (job != null) job.Cancel(); else await RunTranslation(); }

    void SetBusy(bool busy)
    {
        foreach (var element in new Control[] { FromLanguage, ToLanguage, TranslationStyle, SwapButton, PasteButton, ClearButton, HistoryButton, ModelsButton, SettingsButton, InsertButton, GlossariesButton, GlossaryChoice, DocumentFormat, UseContext }) element.IsEnabled = !busy;
        InputText.IsReadOnly = busy;
        TranslateLabel.Text = busy ? "キャンセル" : "翻訳する";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(TranslateButton, TranslateLabel.Text);
        if (!busy) TranslationStyle.IsEnabled = Language(ToLanguage) == "ja";
    }

    async Task RunTranslation()
    {
        if (job != null || modal || quitting || string.IsNullOrWhiteSpace(InputText.Text)) return;
        if (InputText.Text.Length > prefs.Limit) { StatusText.Text = $"入力を {prefs.Limit:N0} 文字以内に分けてください。"; return; }
        if (prefs.Active is not Model model || !prefs.LicenseAccepted) { await OpenModels(); return; }
        if (Language(FromLanguage) == Language(ToLanguage)) { StatusText.Text = "翻訳元と翻訳先に異なる言語を選んでください。"; return; }
        job = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        SetBusy(true); OutputText.Text = "";
        var source = InputText.Text; bool acquired = false;
        try {
            await engineGate.WaitAsync(job.Token); acquired = true;
            await engine.Load(Path.Combine(prefs.Folder, model.file), prefs.ExecutionDevice, new Progress<string>(s => StatusText.Text = s), job.Token);
            StatusText.Text = $"{model.id.ToUpperInvariant()} · {engine.Device} · 翻訳中…";
            var request = new TranslationRequest(source, Language(FromLanguage), Language(ToLanguage), prefs.Style, prefs.GlossaryId, ContextText.Text, prefs.DocumentFormat, prefs.UseContext);
            var terms = GetTerms(request.GlossaryId);
            OutputText.Text = await DocumentTranslation.Translate(request, (text, context, history, token) => engine.Translate(text, request.From, request.To, request.Style, token, context, terms, history), job.Token);
            var inferred = Language(FromLanguage) != "" ? Language(FromLanguage) : LanguageDetection.Guess(source);
            var detected = Language(FromLanguage) == "" ? Lang.List.FirstOrDefault(x => x.Id == inferred)?.Name : null;
            StatusText.Text = $"翻訳完了 · {engine.Device}" + (detected == null ? "" : $" · 原文の推定：{detected}");
            if (prefs.History) { entries.Insert(0, new(source, OutputText.Text, Language(FromLanguage), Language(ToLanguage))); if (entries.Count > 100) entries.RemoveRange(100, entries.Count - 100); SaveLog(); }
        } catch (OperationCanceledException) { if (acquired) engine.Dispose(); StatusText.Text = "翻訳をキャンセルしました。"; }
        catch (Exception ex) { if (acquired) engine.Dispose(); StatusText.Text = ex.Message; }
        finally { if (acquired) engineGate.Release(); job.Dispose(); job = null; SetBusy(false); }
    }

    void SaveLog()
    {
        Directory.CreateDirectory(Preferences.Home);
        File.WriteAllText(LogPath + ".tmp", JsonSerializer.Serialize(entries)); File.Move(LogPath + ".tmp", LogPath, true);
    }

    IReadOnlyList<GlossaryTerm> GetTerms(string id) => id == "" ? [] : glossaries?.Resolve(id) ?? throw new IOException(glossaryError);
    void RefreshGlossaries()
    {
        var selected = prefs.GlossaryId;
        syncingOptions = true;
        var choices = new[] { new GlossaryOption("", "用語集なし") }.Concat(glossaries?.Items.Select(g => new GlossaryOption(g.Id, g.ToString())) ?? []).ToArray();
        GlossaryChoice.ItemsSource = choices;
        GlossaryChoice.SelectedItem = choices.FirstOrDefault(g => g.Id == selected) ?? choices[0];
        prefs.GlossaryId = ((GlossaryOption)GlossaryChoice.SelectedItem).Id;
        syncingOptions = false;
        if (initialized) Options_Changed(GlossaryChoice, null!);
    }
    void Options_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || syncingOptions || job != null) return;
        prefs.GlossaryId = (GlossaryChoice.SelectedItem as GlossaryOption)?.Id ?? "";
        prefs.DocumentFormat = Formats[Math.Max(0, DocumentFormat.SelectedIndex)]; prefs.Save(); OutputText.Text = "";
    }
    void Context_Changed(object sender, RoutedEventArgs e)
    {
        if (!initialized || job != null) return;
        prefs.UseContext = UseContext.IsChecked == true; prefs.Save(); OutputText.Text = "";
    }
    async void Glossaries_Click(object sender, RoutedEventArgs e)
    {
        if (modal || job != null || quitting) return;
        if (glossaries == null) { StatusText.Text = "用語集を開けません。 " + glossaryError; return; }
        modal = true;
        try {
            var manager = new GlossariesDialog(glossaries) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
            dialog = manager; await manager.ShowAsync();
        } catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { dialog = null; modal = false; RefreshGlossaries(); }
    }

    async void History_Click(object sender, RoutedEventArgs e)
    {
        if (modal || job != null) return;
        modal = true;
        try {
            var list = new ListView { ItemsSource = entries.Select(x => x.ToString()).ToArray(), Height = 280, SelectionMode = ListViewSelectionMode.Single };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(list, "HistoryList");
            var panel = new StackPanel { Spacing = 16, MinWidth = 360 };
            panel.Children.Add(new TextBlock { Text = entries.Count == 0 ? "翻訳履歴はありません。設定で履歴の保存を有効にできます。" : $"このPCに保存した翻訳 {entries.Count} 件", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(list);
            var erase = new Button { Content = "履歴を全件削除", IsEnabled = entries.Count > 0 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(erase, "EraseHistoryButton");
            var flyout = new Flyout(); var confirmPanel = new StackPanel { Spacing = 12 };
            confirmPanel.Children.Add(new TextBlock { Text = "履歴をすべて削除しますか？" });
            var confirm = new Button { Content = "削除する" };
            confirm.Click += (_, _) => { try { File.Delete(LogPath); entries.Clear(); list.ItemsSource = null; erase.IsEnabled = false; } catch (Exception ex) { StatusText.Text = ex.Message; } flyout.Hide(); };
            confirmPanel.Children.Add(confirm); flyout.Content = confirmPanel; erase.Flyout = flyout; panel.Children.Add(erase);
            var history = new ContentDialog { Title = "翻訳履歴", Content = panel, PrimaryButtonText = "選択した翻訳を開く", CloseButtonText = "閉じる", IsPrimaryButtonEnabled = false, XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
            list.SelectionChanged += (_, _) => history.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;
            dialog = history;
            if (await history.ShowAsync() == ContentDialogResult.Primary && list.SelectedIndex >= 0) {
                var entry = entries[list.SelectedIndex]; SelectLanguage(FromLanguage, entry.From); SelectLanguage(ToLanguage, entry.To); InputText.Text = entry.Source; OutputText.Text = entry.Result;
            }
        } catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { dialog = null; modal = false; }
    }

    async Task CaptureSelection()
    {
        capturing = true;
        try {
            origin = NativeDesktop.GetForegroundWindow(); NativeDesktop.GetWindowThreadProcessId(origin, out originPid);
            if (originPid == Environment.ProcessId) { origin = 0; InsertButton.Visibility = Visibility.Collapsed; Reveal(); return; }
            var sequence = NativeDesktop.GetClipboardSequenceNumber();
            using var backup = new ClipboardBackup(hwnd); string selected = "";
            for (int i = 0; i < 100 && NativeDesktop.ModifierDown; i++) await Task.Delay(15);
            if (NativeDesktop.GetForegroundWindow() == origin && !NativeDesktop.ModifierDown) {
                NativeDesktop.SendControlKey(0x43);
                for (int i = 0; i < 30 && sequence == NativeDesktop.GetClipboardSequenceNumber(); i++) await Task.Delay(25);
                if (sequence != NativeDesktop.GetClipboardSequenceNumber()) {
                    try { var data = Clipboard.GetContent(); if (data.Contains(StandardDataFormats.Text)) selected = await data.GetTextAsync(); }
                    finally { backup.Restore(hwnd); }
                }
            }
            Reveal(); InsertButton.Visibility = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(selected)) { InputText.Text = selected; await RunTranslation(); }
            else StatusText.Text = "選択文字を取得できませんでした。原文を貼り付けてください。";
        } catch (Exception ex) { Reveal(); StatusText.Text = "手動で貼り付けてください。 " + ex.Message; }
        finally { capturing = false; }
    }

    async void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (origin == 0 || OutputText.Text.Length == 0 || job != null || modal) return;
        modal = true;
        try {
            var confirm = new ContentDialog { Title = "訳文の挿入", Content = "呼び出し元の選択位置に訳文を貼り付けますか？", PrimaryButtonText = "貼り付ける", CloseButtonText = "キャンセル", XamlRoot = Root.XamlRoot, RequestedTheme = Root.RequestedTheme };
            dialog = confirm;
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            NativeDesktop.GetWindowThreadProcessId(origin, out var pid);
            if (pid != originPid || !NativeDesktop.SetForegroundWindow(origin)) { StatusText.Text = "元の画面に戻れませんでした。訳文をコピーしてください。"; return; }
            await Task.Delay(120);
            if (NativeDesktop.GetForegroundWindow() == origin) { Ui.CopyText(OutputText.Text); NativeDesktop.SendControlKey(0x56); }
        } catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { modal = false; dialog = null; }
    }
}
