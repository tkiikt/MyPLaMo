using Microsoft.UI.Xaml.Controls;

namespace MyPLaMo;

public sealed partial class SettingsDialog : ContentDialog
{
    readonly Preferences prefs;
    static string StartupLink => Path.Combine(Environment.GetEnvironmentVariable("MYPLAMO_PROFILE") == null ? Environment.GetFolderPath(Environment.SpecialFolder.Startup) : Preferences.Home, "MyPLaMo.lnk");

    internal SettingsDialog(Preferences prefs)
    {
        this.prefs = prefs; InitializeComponent();
        Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["DefaultContentDialogStyle"];
        GpuToggle.IsOn = prefs.GPU; InputLimit.Value = Math.Clamp(prefs.Limit, 100, 4000);
        ShortcutCombo.SelectedItem = new[] { "T", "Y", "J", "無効" }.Contains(prefs.Shortcut) ? prefs.Shortcut : "無効";
        HistoryToggle.IsOn = prefs.History; StartupToggle.IsOn = File.Exists(StartupLink); DarkToggle.IsOn = prefs.Dark;
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
            prefs.GPU = GpuToggle.IsOn; prefs.Limit = (int)InputLimit.Value; prefs.Shortcut = (string)ShortcutCombo.SelectedItem;
            prefs.History = HistoryToggle.IsOn; prefs.Dark = DarkToggle.IsOn; prefs.Save();
        } catch (Exception ex) { SaveStatus.Text = "設定を保存できませんでした。 " + ex.Message; args.Cancel = true; }
    }
}
