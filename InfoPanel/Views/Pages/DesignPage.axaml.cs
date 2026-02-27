using Avalonia.Controls;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using InfoPanel.ViewModels.Components;
using InfoPanel.Views.Components;
using System;

namespace InfoPanel.Views.Pages
{
    public partial class DesignPage : UserControl
    {
        private readonly DesignPageViewModel _vm;

        public DesignPage()
        {
            InitializeComponent();
            _vm = new DesignPageViewModel();
            DataContext = _vm;

            _vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DesignPageViewModel.SelectedItem))
                {
                    UpdateTypeSpecificEditor();
                }
            };

            // Auto-refresh sensors and configure tabs per platform
            Loaded += (_, _) =>
            {
                _vm.RefreshSensorsCommand.Execute(null);

                if (OperatingSystem.IsLinux())
                {
                    // Default to hwmon tab
                    var tabControl = this.FindControl<TabControl>("SensorTabs");
                    if (tabControl != null)
                        tabControl.SelectedIndex = 1; // hwmon tab
                }
            };
        }

        private void UpdateTypeSpecificEditor()
        {
            var item = _vm.SelectedItem;
            if (item == null)
            {
                TypeSpecificEditor.Content = null;
                return;
            }

            Control? editor = item switch
            {
                SensorDisplayItem => CreateSensorEditor(item),
                ClockDisplayItem or CalendarDisplayItem => CreateDateTimeEditor(item),
                TextDisplayItem => new TextProperties { DataContext = item },
                BarDisplayItem => new BarProperties { DataContext = item },
                GraphDisplayItem => new GraphProperties { DataContext = item },
                DonutDisplayItem => new DonutProperties { DataContext = item },
                GaugeDisplayItem => new GaugeProperties { DataContext = item },
                ShapeDisplayItem => new ShapeProperties { DataContext = item },
                GroupDisplayItem => new GroupProperties { DataContext = item },
                SensorImageDisplayItem => new ImageProperties { DataContext = item },
                ImageDisplayItem => new ImageProperties { DataContext = item },
                _ => null
            };

            TypeSpecificEditor.Content = editor;
        }

        private static Control CreateSensorEditor(DisplayItem item)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextProperties { DataContext = item });
            panel.Children.Add(new SensorProperties { DataContext = item });
            return panel;
        }

        private static Control CreateDateTimeEditor(DisplayItem item)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextProperties { DataContext = item });
            panel.Children.Add(new DateTimeProperties { DataContext = item });
            return panel;
        }

        private void SensorTreeView_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is TreeView treeView && treeView.SelectedItem is TreeItem item && item.Children.Count > 0)
            {
                item.IsExpanded = !item.IsExpanded;
            }
        }
    }
}
