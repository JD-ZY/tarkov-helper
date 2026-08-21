using System.Configuration;
using System.Data;
using System.Windows;

namespace TarkovHelper.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // Last-resort safety net: an unhandled exception on the UI
        // dispatcher thread otherwise crashes the entire app, silently
        // losing quest/position tracking mid-raid over a display-layer
        // edge case in some non-critical feature. Prefer fixing the real
        // bug when one is found (a standalone always-on-top popup for item
        // lookup results was tried and removed after repeatedly crashing
        // on WPF window-deactivation reentrancy - see MainWindow's item
        // lookup hotkey handler for what replaced it), but this stops any
        // remaining edge case from taking down the whole app rather than
        // just the one feature that misbehaved.
        DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
        };
    }
}

