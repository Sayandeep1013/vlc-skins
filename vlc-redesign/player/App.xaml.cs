using System.Windows;

namespace RedesignPlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // "--register-open-with" / "--unregister-open-with": add or remove the Explorer
        // "Open with" entry, then exit without showing the player.
        if (e.Args.Contains("--register-open-with") || e.Args.Contains("--unregister-open-with"))
        {
            if (Core.ShellIntegration.IsPackaged) { Shutdown(0); return; } // the package manifest handles it
            try
            {
                if (e.Args.Contains("--register-open-with")) Core.ShellIntegration.Register();
                else Core.ShellIntegration.Unregister();
            }
            catch (Exception ex) { LogError(ex); Shutdown(1); return; }
            Shutdown(0);
            return;
        }
        DispatcherUnhandledException += (_, ex) =>
        {
            LogError(ex.Exception);
            MessageBox.Show(ex.Exception.Message, "VLC Redesign", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        MainWindow? w;
        try { w = new MainWindow(e.Args); }
        catch (Core.VlcConfig.VlcMissingException ex)
        {
            // Offer the fix instead of a dead-end error.
            var r = MessageBox.Show(ex.Message + "\n\nInstall VLC media player (free, from videolan.org), then open VLC Redesign again." +
                "\n\nOpen the VLC download page now?",
                "VLC Redesign needs VLC", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.videolan.org/vlc/") { UseShellExecute = true });
            Shutdown(1);
            return;
        }
        catch (Exception ex)
        {
            LogError(ex);
            MessageBox.Show(ex.Message, "VLC Redesign", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        MainWindow = w;
        w.Show();
    }

    // Full details go to %TEMP%\vlc-redesign-player.log; the dialog only shows the message.
    public static void LogError(Exception ex)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vlc-redesign-player.log"), $"{DateTime.Now:s} {ex}{Environment.NewLine}{Environment.NewLine}"); }
        catch { }
    }
}
