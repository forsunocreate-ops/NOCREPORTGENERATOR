using CommunityToolkit.WinUI.Animations;
using CommunityToolkit.WinUI.Lottie;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using NOCREPORTGENERATOR.Pages;
using NOCREPORTGENERATOR.Services;
using System;
using System.Collections.Generic;
using Windows.ApplicationModel.DataTransfer;
using WinUIEx;
using WinRT.Interop;

namespace NOCREPORTGENERATOR
{
    public sealed partial class MainWindow : WindowEx
    {
        private readonly Dictionary<string, int> _navOrder = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dashboard"] = 0,
            ["create-tt"] = 1,
            ["live-map"] = 2,
            ["history-tt"] = 3
        };
        private string _currentTag = "dashboard";

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBarDragRegion);
            ConfigureCaptionButtonsTheme();

            Width = 1420;
            Height = 900;

            DeveloperModeToggleSwitch.IsOn = DeveloperDiagnostics.IsDeveloperModeEnabled;
            ViewLogsButton.Visibility = DeveloperDiagnostics.IsDeveloperModeEnabled ? Visibility.Visible : Visibility.Collapsed;
            WindowRootGrid.Loaded += WindowRootGrid_Loaded;

            DashboardNavButton.PointerEntered += NavButton_PointerEntered;
            DashboardNavButton.PointerExited += NavButton_PointerExited;
            CreateTtNavButton.PointerEntered += NavButton_PointerEntered;
            CreateTtNavButton.PointerExited += NavButton_PointerExited;
            LiveMapNavButton.PointerEntered += NavButton_PointerEntered;
            LiveMapNavButton.PointerExited += NavButton_PointerExited;
            HistoryTtNavButton.PointerEntered += NavButton_PointerEntered;
            HistoryTtNavButton.PointerExited += NavButton_PointerExited;

            TryInitializeLottiePulse();
            LogUiStackStatus();
            ImportJobService.StateChanged += ImportJobService_StateChanged;
            Closed += MainWindow_Closed;
            NavigateTo("dashboard");
        }

        private static void LogUiStackStatus()
        {
            try
            {
                var telerikWinFormsType = Type.GetType("Telerik.WinControls.RadControl, Telerik.WinControls", throwOnError: false);
                var telerikStatus = telerikWinFormsType is null
                    ? "installed (legacy WinForms package, no WinUI control surface)"
                    : "loaded";

                DeveloperDiagnostics.LogInfo(
                    "UI stack active: WinUIEx, Lottie, FluentIcons, MaterialIcons, Win2D. TelerikWinCompositeUI: " +
                    telerikStatus);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("MainWindow.LogUiStackStatus", ex);
            }
        }

        private void ConfigureCaptionButtonsTheme()
        {
            try
            {
                var hWnd = WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                var titleBar = appWindow.TitleBar;

                titleBar.BackgroundColor = Colors.Transparent;
                titleBar.ForegroundColor = ColorHelper.FromArgb(255, 18, 49, 74);
                titleBar.InactiveBackgroundColor = Colors.Transparent;
                titleBar.InactiveForegroundColor = ColorHelper.FromArgb(255, 108, 130, 155);

                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonForegroundColor = ColorHelper.FromArgb(255, 18, 49, 74);
                titleBar.ButtonHoverBackgroundColor = ColorHelper.FromArgb(90, 47, 129, 199);
                titleBar.ButtonHoverForegroundColor = ColorHelper.FromArgb(255, 15, 45, 69);
                titleBar.ButtonPressedBackgroundColor = ColorHelper.FromArgb(130, 42, 132, 207);
                titleBar.ButtonPressedForegroundColor = Colors.White;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveForegroundColor = ColorHelper.FromArgb(255, 108, 130, 155);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("MainWindow.ConfigureCaptionButtonsTheme", ex);
            }
        }

        private void NavButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string tag)
            {
                return;
            }

            NavigateTo(tag);
        }

        public void NavigateToModule(string tag)
        {
            NavigateTo(tag);
        }

        private void NavigateTo(string tag)
        {
            Type pageType;
            string pageTitle;

            switch (tag)
            {
                case "dashboard":
                    pageType = typeof(DashboardPage);
                    pageTitle = "Dashboard";
                    break;
                case "create-tt":
                    pageType = typeof(CreateTtPage);
                    pageTitle = "Create TT";
                    break;
                case "live-map":
                    pageType = typeof(LiveMapPage);
                    pageTitle = "Live Map";
                    break;
                case "history-tt":
                    pageType = typeof(HistoryTtPage);
                    pageTitle = "History TT";
                    break;
                default:
                    pageType = typeof(DashboardPage);
                    pageTitle = "Dashboard";
                    break;
            }

            if (ContentFrame.CurrentSourcePageType != pageType)
            {
                var transition = BuildNavigationTransition(tag);
                ContentFrame.Navigate(pageType, null, transition);
            }

            CurrentPageTitleTextBlock.Text = pageTitle;
            WindowSubtitleTextBlock.Text = pageTitle + " module";
            UpdateContentHeaderVisibility(tag);
            UpdateNavSelection(tag);
            _currentTag = tag;
        }

        private void UpdateContentHeaderVisibility(string tag)
        {
            var isLiveMap = string.Equals(tag, "live-map", StringComparison.OrdinalIgnoreCase);
            ContentHeaderRowDefinition.Height = isLiveMap
                ? new GridLength(0)
                : GridLength.Auto;
            ContentHeaderBorder.Visibility = isLiveMap ? Visibility.Collapsed : Visibility.Visible;
            ContentHostBorder.Margin = isLiveMap ? new Thickness(0) : new Thickness(0, 14, 0, 0);
        }

        private NavigationTransitionInfo BuildNavigationTransition(string nextTag)
        {
            if (!_navOrder.TryGetValue(_currentTag, out var currentIndex) ||
                !_navOrder.TryGetValue(nextTag, out var nextIndex))
            {
                return new EntranceNavigationTransitionInfo();
            }

            return nextIndex >= currentIndex
                ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight }
                : new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft };
        }

        private void UpdateNavSelection(string selectedTag)
        {
            ApplyNavButtonState(DashboardNavButton, "dashboard", selectedTag);
            ApplyNavButtonState(CreateTtNavButton, "create-tt", selectedTag);
            ApplyNavButtonState(LiveMapNavButton, "live-map", selectedTag);
            ApplyNavButtonState(HistoryTtNavButton, "history-tt", selectedTag);
        }

        private void ApplyNavButtonState(Button button, string buttonTag, string selectedTag)
        {
            var isSelected = string.Equals(buttonTag, selectedTag, StringComparison.OrdinalIgnoreCase);

            if (isSelected)
            {
                button.Background = (Brush)Application.Current.Resources["ShellNavSelectedBackgroundBrush"];
                button.BorderBrush = (Brush)Application.Current.Resources["ShellNavSelectedBorderBrush"];
                button.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
            else
            {
                button.Background = (Brush)Application.Current.Resources["ShellNavDefaultBackgroundBrush"];
                button.BorderBrush = (Brush)Application.Current.Resources["ShellNavDefaultBorderBrush"];
                button.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
            }
        }

        private void DeveloperModeToggleSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            DeveloperDiagnostics.IsDeveloperModeEnabled = DeveloperModeToggleSwitch.IsOn;
            ViewLogsButton.Visibility = DeveloperModeToggleSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;
            DeveloperDiagnostics.LogInfo("Developer mode " + (DeveloperModeToggleSwitch.IsOn ? "enabled" : "disabled"));
        }

        private async void ViewLogsButton_Click(object sender, RoutedEventArgs e)
        {
            var logTextBox = new TextBox
            {
                Text = DeveloperDiagnostics.GetLogs(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                MinWidth = 900,
                MinHeight = 500
            };
            ScrollViewer.SetVerticalScrollBarVisibility(logTextBox, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(logTextBox, ScrollBarVisibility.Auto);

            var dialog = new ContentDialog
            {
                Title = "Developer Error Logs",
                Content = logTextBox,
                PrimaryButtonText = "Copy All",
                SecondaryButtonText = "Clear Logs",
                CloseButtonText = "Close",
                XamlRoot = Content.XamlRoot
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var package = new DataPackage();
                package.SetText(logTextBox.Text);
                Clipboard.SetContent(package);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                DeveloperDiagnostics.Clear();
            }
        }

        private void WindowRootGrid_Loaded(object sender, RoutedEventArgs e)
        {
            AnimationBuilder.Create()
                .Opacity(to: 1, from: 0, duration: TimeSpan.FromMilliseconds(320), easingType: EasingType.Cubic)
                .Translation(Axis.Y, to: 0, from: 16, duration: TimeSpan.FromMilliseconds(320), easingType: EasingType.Cubic)
                .Start(ShellContentGrid);
        }

        private void TryInitializeLottiePulse()
        {
            try
            {
                var source = new LottieVisualSource
                {
                    UriSource = new Uri("ms-appx:///Assets/Animations/signal-pulse.json")
                };

                var player = new AnimatedVisualPlayer
                {
                    AutoPlay = true,
                    Stretch = Stretch.Uniform,
                    Width = 22,
                    Height = 22,
                    Source = source
                };

                LottiePulseHost.Child = player;
            }
            catch (Exception ex)
            {
                LottiePulseHost.Visibility = Visibility.Collapsed;
                DeveloperDiagnostics.LogError("MainWindow.TryInitializeLottiePulse", ex);
            }
        }

        private void NavButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not UIElement element)
            {
                return;
            }

            AnimationBuilder.Create()
                .Scale(to: 1.02, duration: TimeSpan.FromMilliseconds(150), easingType: EasingType.Cubic)
                .Start(element);
        }

        private void NavButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not UIElement element)
            {
                return;
            }

            AnimationBuilder.Create()
                .Scale(to: 1.0, duration: TimeSpan.FromMilliseconds(150), easingType: EasingType.Cubic)
                .Start(element);
        }

        private void ImportJobService_StateChanged(ImportJobState state)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!state.IsActive)
                {
                    ImportProgressBorder.Visibility = Visibility.Collapsed;
                    ImportProgressBar.IsIndeterminate = false;
                    ImportProgressBar.Value = 0;
                    ImportProgressTextBlock.Text = string.Empty;
                    CancelImportButton.IsEnabled = false;
                    return;
                }

                ImportProgressBorder.Visibility = Visibility.Visible;
                ImportProgressTextBlock.Text = state.Message;
                ImportProgressBar.IsIndeterminate = state.IsIndeterminate;
                if (!state.IsIndeterminate)
                {
                    ImportProgressBar.Value = state.Percent;
                }

                CancelImportButton.IsEnabled = state.CanCancel;
            });
        }

        private void CancelImportButton_Click(object sender, RoutedEventArgs e)
        {
            CancelImportButton.IsEnabled = false;
            ImportProgressTextBlock.Text = "Membatalkan import...";
            ImportJobService.Cancel();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            ImportJobService.StateChanged -= ImportJobService_StateChanged;
        }
    }
}
