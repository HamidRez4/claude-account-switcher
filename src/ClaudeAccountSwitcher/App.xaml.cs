using System.Windows;
using System.Windows.Threading;

namespace ClaudeAccountSwitcher;

public partial class App : Application
{
    void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "Claude Account Switcher", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
