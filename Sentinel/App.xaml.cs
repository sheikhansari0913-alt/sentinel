// Sentinel/App.xaml.cs

using System.Windows;

namespace Sentinel;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Surface unhandled exceptions clearly during the demo.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.ToString(), "Sentinel — Unhandled",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
