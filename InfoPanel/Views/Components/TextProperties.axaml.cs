using Avalonia.Controls;
using SkiaSharp;
using System.Linq;

namespace InfoPanel.Views.Components;

public partial class TextProperties : UserControl
{
    public TextProperties()
    {
        InitializeComponent();
        FontComboBox.Tag = SKFontManager.Default.GetFontFamilies().OrderBy(f => f).ToList();
    }
}
