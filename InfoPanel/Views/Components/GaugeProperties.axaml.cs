using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InfoPanel.Models;
using System.Linq;

namespace InfoPanel.Views.Components;

public partial class GaugeProperties : UserControl
{
    public GaugeProperties()
    {
        InitializeComponent();
    }

    private async void AddImage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not GaugeDisplayItem gauge) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Gauge Image",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.svg"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });

        foreach (var file in files)
        {
            var img = new ImageDisplayItem
            {
                FilePath = file.Path.LocalPath,
                PersistentCache = true
            };
            gauge.Images.Add(img);
        }
    }

    private void RemoveImage_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GaugeDisplayItem gauge && ImagesList.SelectedItem is ImageDisplayItem img)
        {
            gauge.Images.Remove(img);
        }
    }

    private void MoveImageUp_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GaugeDisplayItem gauge && ImagesList.SelectedItem is ImageDisplayItem img)
        {
            var idx = gauge.Images.IndexOf(img);
            if (idx > 0)
            {
                gauge.Images.Move(idx, idx - 1);
            }
        }
    }

    private void MoveImageDown_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is GaugeDisplayItem gauge && ImagesList.SelectedItem is ImageDisplayItem img)
        {
            var idx = gauge.Images.IndexOf(img);
            if (idx < gauge.Images.Count - 1)
            {
                gauge.Images.Move(idx, idx + 1);
            }
        }
    }
}
