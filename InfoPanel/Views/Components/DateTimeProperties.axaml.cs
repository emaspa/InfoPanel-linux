using Avalonia.Controls;
using Avalonia.Interactivity;
using InfoPanel.Models;
using System;

namespace InfoPanel.Views.Components;

public partial class DateTimeProperties : UserControl
{
    public DateTimeProperties()
    {
        InitializeComponent();
        FormatTextBox.TextChanged += FormatTextBox_TextChanged;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateBindings();
    }

    private void UpdateBindings()
    {
        if (DataContext is ClockDisplayItem clock)
        {
            FormatTextBox.Text = clock.Format;
            TimeTokensExpander.IsVisible = true;
            DateTokensExpander.IsVisible = true;
        }
        else if (DataContext is CalendarDisplayItem calendar)
        {
            FormatTextBox.Text = calendar.Format;
            DateTokensExpander.IsVisible = true;
            TimeTokensExpander.IsVisible = false;
        }
        UpdatePreview();
    }

    private void FormatTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var format = FormatTextBox.Text;
        if (string.IsNullOrEmpty(format)) return;

        try
        {
            if (DataContext is ClockDisplayItem clock)
                clock.Format = format;
            else if (DataContext is CalendarDisplayItem calendar)
                calendar.Format = format;
            UpdatePreview();
        }
        catch { }
    }

    private void UpdatePreview()
    {
        try
        {
            var format = FormatTextBox.Text;
            if (!string.IsNullOrEmpty(format))
                PreviewText.Text = DateTime.Now.ToString(format);
        }
        catch
        {
            PreviewText.Text = "(invalid format)";
        }
    }

    private void TemplateCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TemplateCombo.SelectedItem is ComboBoxItem item && item.Content is string template)
        {
            if (template != "Custom")
            {
                FormatTextBox.Text = template;
            }
        }
    }

    private void InsertToken_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string token)
        {
            var caretIndex = FormatTextBox.CaretIndex;
            var text = FormatTextBox.Text ?? "";
            FormatTextBox.Text = text.Insert(caretIndex, token);
            FormatTextBox.CaretIndex = caretIndex + token.Length;
        }
    }
}
