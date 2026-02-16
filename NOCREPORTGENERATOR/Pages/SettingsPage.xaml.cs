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
            GeminiApiKeyPasswordBox.Password = AppSettingsService.GetGeminiApiKey();
            StatusTextBlock.Text = "API key saat ini " + (string.IsNullOrWhiteSpace(GeminiApiKeyPasswordBox.Password) ? "belum diisi." : "sudah terisi.");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            AppSettingsService.SetGeminiApiKey(GeminiApiKeyPasswordBox.Password);
            StatusTextBlock.Text = "API key Gemini tersimpan.";
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            GeminiApiKeyPasswordBox.Password = string.Empty;
            AppSettingsService.SetGeminiApiKey(string.Empty);
            StatusTextBlock.Text = "API key Gemini dihapus.";
        }
    }
}
