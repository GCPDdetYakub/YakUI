using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using YakUI.App.Services;
using YakUI.App.ViewModels;
using YakUI.App.Views;

namespace YakUI.App;

public sealed class App : Application
{
    private AppHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // В тестах (Avalonia.Headless) окно создаётся тестом с фейковыми сервисами.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _host = AppHost.Create(new AvaloniaUiDispatcher());
            var window = new MainWindow { DataContext = _host.Get<MainWindowViewModel>() };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _host.Dispose();
            window.Activated += (_, _) => _host.OnWindowActivated(); // FR-55a

            // FR-89e: перед закрытием вернуть hosts из копии сеанса (один запрос UAC), затем закрыть окно.
            var hostsChecked = false;
            window.Closing += async (_, e) =>
            {
                if (hostsChecked)
                    return;
                e.Cancel = true;
                hostsChecked = true;
                try
                {
                    await _host.Get<HostsViewModel>().OnClosingAsync();
                }
                finally
                {
                    window.Close();
                }
            };

            // NFR-05a: повторный запуск просит показать уже открытое окно.
            if (Program.Instance is { } instance)
                instance.ActivationRequested += () => Dispatcher.UIThread.Post(() => BringToFront(window));

            _host.Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Show();
        window.Activate();
        window.Topmost = true; // приём «поднять поверх» без постоянного Topmost
        window.Topmost = false;
    }
}
