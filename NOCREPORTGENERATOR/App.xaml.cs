using Microsoft.UI.Xaml;
using NOCREPORTGENERATOR.Services;
using QuestPDF.Infrastructure;
using System;
using System.Text;
using System.Threading.Tasks;

namespace NOCREPORTGENERATOR
{
    public partial class App : Application
    {
        private Window? _window;
        public static Window? MainAppWindow { get; private set; }

        public App()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            QuestPDF.Settings.License = LicenseType.Community;
            InitializeComponent();
            UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            _window = new MainWindow();
            MainAppWindow = _window;
            _window.Activate();
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            DeveloperDiagnostics.LogError("App.UnhandledException", e.Exception);
        }

        private static void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            DeveloperDiagnostics.LogError("AppDomain.CurrentDomain.UnhandledException", e.ExceptionObject as Exception);
        }

        private static void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            DeveloperDiagnostics.LogError("TaskScheduler.UnobservedTaskException", e.Exception);
        }
    }
}
