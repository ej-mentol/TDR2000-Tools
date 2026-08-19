using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace TDR.Tools;

public partial class App : Application
{
    public override void Initialize()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogUnhandledException("Unhandled AppDomain Exception", ex);
            }
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogUnhandledException("Unobserved Task Exception", e.Exception);
            e.SetObserved();
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void LogUnhandledException(string context, Exception ex)
    {
        string msg = $"[CRITICAL UI EXCEPTION] {context}: {ex.GetType().Name} - {ex.Message}\n{ex.StackTrace}";
        System.Diagnostics.Debug.WriteLine(msg);
        Services.LogService.Instance.Error(msg);
    }
}