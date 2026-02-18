using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace NOCREPORTGENERATOR.Services
{
    public static class EditableComboBoxContainsFilterHelper
    {
        private static readonly ConditionalWeakTable<ComboBox, ComboState> States = new();

        public static void Attach(
            ComboBox comboBox,
            Func<IReadOnlyList<string>> sourceProvider,
            string? emptyFallbackItem = null)
        {
            if (comboBox is null || sourceProvider is null)
            {
                return;
            }

            if (!States.TryGetValue(comboBox, out var state))
            {
                state = new ComboState();
                States.Add(comboBox, state);
                comboBox.Loaded += ComboBox_Loaded;
                comboBox.DropDownOpened += ComboBox_DropDownOpened;
                comboBox.Unloaded += ComboBox_Unloaded;
            }

            state.SourceProvider = sourceProvider;
            state.EmptyFallbackItem = emptyFallbackItem;
        }

        public static void Refresh(ComboBox comboBox)
        {
            if (comboBox is null || !States.TryGetValue(comboBox, out var state))
            {
                return;
            }

            ApplyFilter(comboBox, state, comboBox.Text);
        }

        private static void ComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox comboBox || !States.TryGetValue(comboBox, out var state))
            {
                return;
            }

            EnsureEditorHooked(comboBox, state);
            ApplyFilter(comboBox, state, comboBox.Text);
        }

        private static void ComboBox_DropDownOpened(object? sender, object e)
        {
            if (sender is not ComboBox comboBox || !States.TryGetValue(comboBox, out var state))
            {
                return;
            }

            ApplyFilter(comboBox, state, comboBox.Text);
        }

        private static void ComboBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox comboBox || !States.TryGetValue(comboBox, out var state))
            {
                return;
            }

            if (state.Editor is not null)
            {
                state.Editor.TextChanged -= state.EditorTextChangedHandler;
                state.Editor = null;
            }
        }

        private static void EnsureEditorHooked(ComboBox comboBox, ComboState state)
        {
            if (state.Editor is not null)
            {
                return;
            }

            var editor = FindDescendant<TextBox>(comboBox);
            if (editor is null)
            {
                return;
            }

            state.Editor = editor;
            state.EditorTextChangedHandler = (_, _) =>
            {
                if (state.IsUpdatingItems)
                {
                    return;
                }

                ApplyFilter(comboBox, state, state.Editor?.Text);
                comboBox.IsDropDownOpen = true;
            };
            editor.TextChanged += state.EditorTextChangedHandler;
        }

        private static void ApplyFilter(ComboBox comboBox, ComboState state, string? rawText)
        {
            var source = state.SourceProvider?.Invoke() ?? Array.Empty<string>();
            var allItems = source
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var keyword = rawText?.Trim() ?? string.Empty;
            var filtered = string.IsNullOrWhiteSpace(keyword)
                ? allItems
                : allItems
                    .Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (filtered.Count == 0 && !string.IsNullOrWhiteSpace(state.EmptyFallbackItem))
            {
                filtered.Add(state.EmptyFallbackItem);
            }

            state.IsUpdatingItems = true;
            try
            {
                comboBox.ItemsSource = filtered;
            }
            finally
            {
                state.IsUpdatingItems = false;
            }
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is null)
            {
                return null;
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T matched)
                {
                    return matched;
                }

                var nested = FindDescendant<T>(child);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        private sealed class ComboState
        {
            public Func<IReadOnlyList<string>>? SourceProvider { get; set; }
            public string? EmptyFallbackItem { get; set; }
            public TextBox? Editor { get; set; }
            public TextChangedEventHandler? EditorTextChangedHandler { get; set; }
            public bool IsUpdatingItems { get; set; }
        }
    }
}
