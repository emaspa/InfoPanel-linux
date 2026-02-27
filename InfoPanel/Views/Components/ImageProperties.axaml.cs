using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using InfoPanel.Models;
using System.Linq;

namespace InfoPanel.Views.Components;

public partial class ImageProperties : UserControl
{
    public ImageProperties()
    {
        InitializeComponent();
    }

    private async void BrowseFile_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Image",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.svg", "*.webp"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });

        if (files.Count > 0 && DataContext is ImageDisplayItem item)
        {
            item.FilePath = files[0].Path.LocalPath;
        }
    }
}
