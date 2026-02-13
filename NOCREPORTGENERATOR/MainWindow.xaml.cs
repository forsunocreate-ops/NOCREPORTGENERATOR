using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOCREPORTGENERATOR.Pages;

namespace NOCREPORTGENERATOR
{
    public sealed partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            ContentFrame.Navigate(typeof(DashboardPage));
            AppNavigationView.SelectedItem = AppNavigationView.MenuItems[0];
        }

        private void AppNavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.InvokedItemContainer?.Tag is not string tag)
            {
                return;
            }

            switch (tag)
            {
                case "dashboard":
                    ContentFrame.Navigate(typeof(DashboardPage));
                    break;
                case "create-tt":
                    ContentFrame.Navigate(typeof(CreateTtPage));
                    break;
                case "live-map":
                    ContentFrame.Navigate(typeof(LiveMapPage));
                    break;
            }
        }
    }
}
