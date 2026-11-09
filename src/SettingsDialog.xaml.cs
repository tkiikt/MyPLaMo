using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace MyPLaMo;

public sealed partial class SettingsDialog : ContentDialog
{
    readonly Preferences prefs;
    readonly Engine engine;
    readonly CancellationTokenSource discovery;
    ComputeDevice[] devices = [];
    sealed record Choice(string Key, string Label) { public override string ToString() => Label; }
    static string StartupLink => Path.Combine(Environment.GetEnvironmentVariable("MYPLAMO_PROFILE") == null ? Environment.GetFolderPath(Environment.SpecialFolder.Startup) : Preferences.Home, "MyPLaMo.lnk");

    internal SettingsDialog(Preferences prefs, Engine engine, CancellationToken lifetime)
    {
        this.prefs = prefs; this.engine = engine;
        discovery = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        InitializeComponent();
        Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 560d;
        SetChoices(prefs.ExecutionDevice);
        InputLimit.Value = Math.Clamp(prefs.Limit, 100, 4000);
        ShortcutCombo.SelectedItem = new[] { "T", "Y", "J", "無効" }.Contains(prefs.Shortcut) ? prefs.Shortcut : "無効";
        HistoryToggle.IsOn = prefs.History; StartupToggle.IsOn = File.Exists(StartupLink); DarkToggle.IsOn = prefs.Dark;
        Opened += async (_, _) => {
            SettingsScroll.MaxHeight = Math.Max(240, Math.Min(620, XamlRoot.Size.Height - 200));
            await RefreshDevices();
        };
        Closed += (_, _) => { discovery.Cancel(); discovery.Dispose(); };
    }

    void SetChoices(string selected)
    {
        var choices = new List<Choice> { new("auto", "自動（GPU優先）"), new("cpu", "CPUのみ") };
        choices.AddRange(devices.Select(x => new Choice(x.Key, x.Label)));
        if (!choices.Any(x => x.Key == selected)) choices.Add(new(selected, (string.IsNullOrWhiteSpace(prefs.ComputeDeviceLabel) ? "保存済みのGPU" : prefs.ComputeDeviceLabel) + "〈未検出〉"));
        DeviceCombo.ItemsSource = choices;
        DeviceCombo.SelectedItem = choices.Single(x => x.Key == selected);
    }

    async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevices();
    async Task RefreshDevices()
    {
        RefreshDevicesButton.IsEnabled = false;
        DeviceStatus.Text = "利用可能なGPUを確認しています…";
        var ct = discovery.Token;
        try {
            var catalog = await engine.ListDevices(ct);
            if (ct.IsCancellationRequested) return;
            var selected = (DeviceCombo.SelectedItem as Choice)?.Key ?? prefs.ExecutionDevice;
            devices = catalog.Devices; SetChoices(selected);
            DeviceStatus.Text = devices.Length == 0 ? "利用可能なGPUが見つかりません。CPUで実行できます。" : "GPU一覧を更新しました。保存すると読み込み済みモデルを解放します。";
            if (catalog.Errors.Length != 0) DeviceStatus.Text += " " + string.Join(" ", catalog.Errors);
        } catch (OperationCanceledException) { }
        catch (Exception) { if (!ct.IsCancellationRequested) DeviceStatus.Text = "GPU一覧を取得できませんでした。CPUは引き続き選択できます。"; }
        finally { if (!ct.IsCancellationRequested) RefreshDevicesButton.IsEnabled = true; }
    }

    void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceHint == null) return;
        var key = (DeviceCombo.SelectedItem as Choice)?.Key ?? "auto";
        DeviceHint.Text = key switch {
            "cpu" => "GPUにモデルを配置しません。画像生成などにGPUメモリを空けたいときに選んでください。",
            "auto" => "CUDA、Vulkan、CPUの順に利用可能な実行先を選びます。",
            _ when devices.FirstOrDefault(x => x.Key == key)?.Integrated == true => "内蔵GPUを使います。共有メモリを使用し、CPUのみより遅くなる場合があります。",
            _ => "このGPUと計算方式だけを使います。利用できない場合はCPUで実行し、画面に表示します。"
        };
    }

    void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (!double.IsFinite(InputLimit.Value) || InputLimit.Value is < 100 or > 4000) { SaveStatus.Text = "入力上限は100〜4,000文字で指定してください。"; args.Cancel = true; return; }
        try {
            if (StartupToggle.IsOn) {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
                try {
                    dynamic shortcut = shell.CreateShortcut(StartupLink);
                    try { shortcut.TargetPath = Environment.ProcessPath!; shortcut.WorkingDirectory = AppContext.BaseDirectory; shortcut.Arguments = "--tray"; shortcut.Save(); }
                    finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut); }
                } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
            } else if (File.Exists(StartupLink)) File.Delete(StartupLink);
            var choice = (Choice)DeviceCombo.SelectedItem;
            var label = devices.FirstOrDefault(x => x.Key == choice.Key)?.Label ?? (choice.Key == prefs.ExecutionDevice ? prefs.ComputeDeviceLabel : choice.Label);
            prefs.ComputeDevice = choice.Key; prefs.ComputeDeviceLabel = label;
            prefs.GPU = choice.Key != "cpu"; prefs.Limit = (int)InputLimit.Value; prefs.Shortcut = (string)ShortcutCombo.SelectedItem;
            prefs.History = HistoryToggle.IsOn; prefs.Dark = DarkToggle.IsOn; prefs.Save();
        } catch (Exception ex) { SaveStatus.Text = "設定を保存できませんでした。 " + ex.Message; args.Cancel = true; }
    }
}
