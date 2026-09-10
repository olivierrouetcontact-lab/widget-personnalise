using System;
using System.Windows;

namespace MailWidget;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(
                args.Exception.Message,
                "Gmail sur le bureau",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            args.Handled = true;
        };

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (Array.Exists(e.Args, argument =>
                string.Equals(argument, "--manage", StringComparison.OrdinalIgnoreCase)))
        {
            window.ShowManagerFromStartup();
        }
    }
}
