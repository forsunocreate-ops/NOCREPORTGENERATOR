using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOCREPORTGENERATOR.Services;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class AutomationRulesPage : Page
    {
        private readonly AdvancedOpsPageController _controller;

        public AutomationRulesPage()
        {
            InitializeComponent();
            NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
            _controller = new AdvancedOpsPageController(
                this,
                OpsPageKind.AutomationRules,
                "settings",
                SummaryTextBlock,
                LastUpdatedTextBlock,
                CommandTextBox,
                LeftListView,
                RightListView,
                Kpi1LabelTextBlock,
                Kpi1ValueTextBlock,
                Kpi2LabelTextBlock,
                Kpi2ValueTextBlock,
                Kpi3LabelTextBlock,
                Kpi3ValueTextBlock,
                TrendChart);

            Loaded += Page_Loaded;
            Unloaded += Page_Unloaded;
        }

        private async void Page_Loaded(object sender, RoutedEventArgs e)
        {
            await _controller.OnLoadedAsync();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            _controller.OnUnloaded();
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _controller.OnRefreshClicked();
        }

        private void CommandTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _controller.OnCommandTextChanged();
        }

        private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _controller.OnListSelectionChanged(sender);
        }

        private async void OpenActionButton_Click(object sender, RoutedEventArgs e)
        {
            await _controller.OnOpenActionClickedAsync();
        }
    }
}
