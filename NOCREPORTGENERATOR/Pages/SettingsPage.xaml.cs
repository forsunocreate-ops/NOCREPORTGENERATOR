using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NOCREPORTGENERATOR.Services;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsPage()
        {
            InitializeComponent();
            GeminiApiKeyTextBox.Text = AppSettingsService.GetGeminiApiKey();
            StatusTextBlock.Text = "API key saat ini " + (string.IsNullOrWhiteSpace(GeminiApiKeyTextBox.Text) ? "belum diisi." : "sudah terisi.");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsService.SetGeminiApiKey(GeminiApiKeyTextBox.Text);
            StatusTextBlock.Text = "API key Gemini tersimpan.";
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            GeminiApiKeyTextBox.Text = string.Empty;
            AppSettingsService.SetGeminiApiKey(string.Empty);
            StatusTextBlock.Text = "API key Gemini dihapus.";
        }
    }
}
