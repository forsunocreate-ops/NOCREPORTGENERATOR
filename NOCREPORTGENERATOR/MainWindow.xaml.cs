using CommunityToolkit.WinUI.Animations;
using CommunityToolkit.WinUI.Lottie;
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

namespace NOCREPORTGENERATOR
{
    public sealed partial class MainWindow : WindowEx
    {
        private readonly Dictionary<string, int> _navOrder = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dashboard"] = 0,
            ["create-tt"] = 1,
            ["live-map"] = 2
        };
        private string _currentTag = "dashboard";

        public MainWindow()
        {
            InitializeComponent();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBarDragRegion);

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

            TryInitializeLottiePulse();
            LogUiStackStatus();
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
                    "UI stack active: WinUIEx, Lottie, FluentIcons, MaterialIcons, Win2D, Syncfusion. TelerikWinCompositeUI: " +
                    telerikStatus);
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogError("MainWindow.LogUiStackStatus", ex);
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
            UpdateNavSelection(tag);
            _currentTag = tag;
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
                button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
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
    }
}
