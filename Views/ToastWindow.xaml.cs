using System;
using System.Media;
using System.Windows;
using System.Windows.Threading;

namespace SmartClipVault.Views;

public partial class ToastWindow : Window
{
    private readonly DispatcherTimer _closeTimer;

    private ToastWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            Close();
        };

        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - ActualWidth - 16;
            Top = workArea.Bottom - ActualHeight - 16;
            _closeTimer.Start();
        };
    }

    public static void ShowToast(string message, bool? playSound = null)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var shouldPlay = playSound ?? ((Application.Current as App)?.Settings?.Current.PlayToastSound ?? true);
        if (shouldPlay)
        {
            try
            {
                SystemSounds.Asterisk.Play();
            }
            catch
            {
                // Ignore audio device errors
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
            return;

        void ShowCore()
        {
            var toast = new ToastWindow(message);
            toast.Show();
        }

        if (dispatcher.CheckAccess())
            ShowCore();
        else
            dispatcher.BeginInvoke(ShowCore);
    }
}
