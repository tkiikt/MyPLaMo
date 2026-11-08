using Microsoft.UI.Xaml;
using System.Security.Cryptography;
using System.Text;

namespace MyPLaMo;

public partial class App : Application
{
    Mutex? instance;
    MainWindow? window;

    public App()
    {
        InitializeComponent();
        RequestedTheme = Preferences.Load().Dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        UnhandledException += (_, e) => {
            Directory.CreateDirectory(Preferences.Home);
            File.AppendAllText(Path.Combine(Preferences.Home, "error.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Separate test profiles can run beside the user's installed copy.
        var profile = Environment.GetEnvironmentVariable("MYPLAMO_PROFILE");
        var suffix = profile == null ? "" : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile)))[..16];
        instance = new Mutex(true, "Local\\MyPLaMo.Desktop" + suffix, out var first);
        if (!first) {
            NativeDesktop.ShowAlreadyRunning();
            instance.Dispose();
            Exit();
            return;
        }
        window = new MainWindow(Environment.GetCommandLineArgs().Contains("--tray"));
        window.Closed += (_, _) => { instance.ReleaseMutex(); instance.Dispose(); };
        window.Activate();
    }
}
