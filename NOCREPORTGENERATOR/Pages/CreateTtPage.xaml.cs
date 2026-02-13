using Microsoft.UI.Xaml.Controls;
using System;
using System.Globalization;

namespace NOCREPORTGENERATOR.Pages
{
    public sealed partial class CreateTtPage : Page
    {
        public CreateTtPage()
        {
            InitializeComponent();

            var now = DateTimeOffset.Now;
            OccurDatePicker.Date = now;
            OccurTimePicker.Time = now.TimeOfDay;
            DispatchDatePicker.Date = now;
            DispatchTimePicker.Time = now.TimeOfDay;

            UpdateTemplatePreview();
        }

        private void InputChanged(object sender, TextChangedEventArgs e)
        {
            UpdateTemplatePreview();
        }

        private void OccurDatePicker_DateChanged(object sender, DatePickerValueChangedEventArgs args)
        {
            UpdateTemplatePreview();
        }

        private void OccurTimePicker_TimeChanged(object sender, TimePickerValueChangedEventArgs args)
        {
            UpdateTemplatePreview();
        }

        private void DispatchDatePicker_DateChanged(object sender, DatePickerValueChangedEventArgs args)
        {
            UpdateTemplatePreview();
        }

        private void DispatchTimePicker_TimeChanged(object sender, TimePickerValueChangedEventArgs args)
        {
            UpdateTemplatePreview();
        }

        private void UpdateTemplatePreview()
        {
            var title = string.IsNullOrWhiteSpace(TitleTextBox.Text) ? "Judul TT" : TitleTextBox.Text.Trim();
            var occurTime = FormatDateTime(OccurDatePicker.Date, OccurTimePicker.Time);
            var dispatchTime = FormatDateTime(DispatchDatePicker.Date, DispatchTimePicker.Time);
            var pic = PicTextBox.Text?.Trim() ?? string.Empty;
            var rootCause = RootCauseTextBox.Text?.Trim() ?? string.Empty;
            var cutPoint = CutPointTextBox.Text?.Trim() ?? string.Empty;
            var coordinate = CoordinateTextBox.Text?.Trim() ?? string.Empty;
            var updateProgress = UpdateProgressTextBox.Text?.Trim() ?? string.Empty;

            var preview = "*" + title + "*" + Environment.NewLine +
                "Occur Time = " + occurTime + Environment.NewLine +
                "Dispacth Time = " + dispatchTime + Environment.NewLine +
                "PIC = " + pic + Environment.NewLine +
                "Rootcause = " + rootCause + Environment.NewLine +
                "Cut Point = " + cutPoint + Environment.NewLine +
                (string.IsNullOrWhiteSpace(coordinate) ? string.Empty : "Coordinate = " + coordinate + Environment.NewLine) +
                "Update Progress" + Environment.NewLine +
                updateProgress;

            TemplatePreviewTextBox.Text = preview;
        }

        private static string FormatDateTime(DateTimeOffset date, TimeSpan time)
        {
            var value = new DateTime(date.Year, date.Month, date.Day, time.Hours, time.Minutes, 0);
            return value.ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);
        }
    }
}
