namespace MyPLaMo;
static class Program
{
    [STAThread] static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if(Preferences.Load().Dark)Ui.SetDark();
        using var mutex=new Mutex(true,"Local\\MyPLaMo.Desktop",out var first);
        if(!first){MessageBox.Show("MyPLaMo は起動中です。通知領域のアイコンから開けます。","MyPLaMo");return;}
        Application.Run(new MainWindow(args.Contains("--tray")));
    }
}
