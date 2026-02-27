using Avalonia.Controls;
using Avalonia.Interactivity;
using InfoPanel.Models;

namespace InfoPanel.Views.Components;

public partial class CommonProperties : UserControl
{
    public CommonProperties()
    {
        InitializeComponent();
    }

    private int MoveStep => (int)(MoveStepBox.Value ?? 1);

    private void NudgeUp_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DisplayItem item)
            item.Y -= MoveStep;
    }

    private void NudgeDown_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DisplayItem item)
            item.Y += MoveStep;
    }

    private void NudgeLeft_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DisplayItem item)
            item.X -= MoveStep;
    }

    private void NudgeRight_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DisplayItem item)
            item.X += MoveStep;
    }
}
