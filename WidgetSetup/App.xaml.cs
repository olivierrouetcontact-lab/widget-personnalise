using System.Windows;

namespace WidgetSetup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new SetupWindow();
        MainWindow = window;
        window.Show();
    }
}
