using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;
using InfoPanel.Models;
using InfoPanel.ViewModels;
using InfoPanel.Views.Pages;
using Xunit;
using Xunit.Abstractions;

namespace InfoPanel.App.Tests;

[Collection("AppState")]
public sealed class PickerLayoutTests(HeadlessUi ui, ITestOutputHelper output) : IClassFixture<HeadlessUi>
{
    private const string DiskName = "WD_BLACK SN850X 4000GB (…0995, nvme0n1) Read Latency";

    [Fact]
    public Task DesignerSplitterMinimumLeavesRoomForIndentedValues() => ui.Run(() =>
    {
        var page = new DesignerPage();
        var panel = (DockPanel)page.Content!;
        var grid = Assert.Single(panel.Children.OfType<Grid>());
        panel.Children.Remove(grid);
        Assert.Single(grid.Children.OfType<TabControl>()).SelectedIndex = 1;
        var tree = page.FindControl<TreeView>("SensorTree")!;
        var leaf = new SensorTreeItem { Name = DiskName, SensorId = "disk-latency", Value = "123.4", Unit = "ms" };
        var branch = leaf;
        for (var level = 0; level < 5; level++)
        {
            var parent = new SensorTreeItem { Name = "Hardware", IsExpanded = true };
            parent.Children.Add(branch);
            branch = parent;
        }
        tree.ItemsSource = new[] { branch };
        using var root = HeadlessUi.CreateRoot();
        root.Content = grid;
        root.Prepare();
        // The splitter must stop at a useful minimum, even if asked to collapse.
        grid.ColumnDefinitions[0].Width = new GridLength(0);
        root.Measure(new Size(1600, 1000));
        root.Arrange(new Rect(0, 0, 1600, 1000));
        root.UpdateLayout();
        Assert.InRange(tree.Bounds.Width, 180, 260);
        AssertValueInViewport(tree, leaf, "Designer splitter minimum");
    });

    [Fact]
    public Task LayerStatusStaysInsideViewport_WhenNameAndCoordinatesAreLong() => ui.Run(() =>
    {
        var page = new DesignerPage();
        var tree = page.FindControl<TreeView>("LayersTree")!;
        ((Panel)tree.Parent!).Children.Remove(tree);
        var layer = new TextDisplayItem
        {
            Name = "A very long but realistic sensor layer name",
            X = 1200, Y = 1000, Hidden = true, IsLocked = true
        };
        tree.ItemsSource = new[] { layer };
        using var root = HeadlessUi.CreateRoot();
        root.Content = tree;
        root.Prepare();
        foreach (var width in new[] { 260, 180, 830, 260 })
        {
            root.Measure(new Size(width, 600));
            root.Arrange(new Rect(0, 0, width, 600));
            root.UpdateLayout();
            foreach (var label in new[] { "H", "L" })
            {
                var status = Assert.Single(tree.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == label);
                var bounds = new Rect(status.Bounds.Size).TransformToAABB(status.TransformToVisual(tree)!.Value);
                Assert.True(bounds.Right <= tree.Bounds.Width, $"{status.Text} ends at {bounds.Right} in {width}px tree");
            }
            var scroll = Assert.Single(tree.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(scroll.Extent.Width <= scroll.Viewport.Width);
        }
    });

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 3)]
    [InlineData(true, 5)]
    [InlineData(false, 0)]
    [InlineData(false, 3)]
    [InlineData(false, 5)]
    public Task ValuesStayInsideViewport_WhenNamesAreLongAndPanelResizes(bool designer, int depth) => ui.Run(() =>
    {
        // Use the compiled production tree, including its styles and template.
        // Detach it before mounting so page lifecycle cannot replace the fixture
        // with the machine's live sensors or start polling/timers.
        UserControl page = designer ? new DesignerPage() : new SensorsPage();
        var tree = page.FindControl<TreeView>(designer ? "SensorTree" : "Tree")!;
        ((Panel)tree.Parent!).Children.Remove(tree);
        var leaf = new SensorTreeItem { Name = DiskName, SensorId = "disk-latency", Value = "12.3", Unit = "ms" };
        var shortLeaf = new SensorTreeItem { Name = "Core 8", SensorId = "core-8", Value = "48", Unit = "°C" };
        // Enough rows to exercise a vertical scrollbar as well as indentation.
        SensorTreeItem[] items = [leaf, shortLeaf, .. Enumerable.Range(0, 24).Select(index =>
            new SensorTreeItem { Name = $"Core {index}", SensorId = $"other-core-{index}", Value = "50", Unit = "°C" })];
        for (var level = depth - 1; level >= 0; level--)
        {
            var parent = new SensorTreeItem { Name = level == 0 ? "Hardware" : "System Block I/O", IsExpanded = true };
            foreach (var child in items) parent.Children.Add(child);
            items = [parent];
        }
        tree.ItemsSource = items;
        using var root = HeadlessUi.CreateRoot();
        root.Content = tree;
        root.Prepare();

        // Cover the default 270px column (246px after theme padding) and the
        // approximately 260px viewport reported in the installed application.
        // Resize the same tree both ways to exercise the splitter's layout path.
        foreach (var width in new[] { 260, 246, 180, 830, 260 })
        {
            root.Measure(new Size(width, 600));
            root.Arrange(new Rect(0, 0, width, 600));
            root.UpdateLayout();
            foreach (var sensor in new[] { leaf, shortLeaf })
                AssertValueInViewport(tree, sensor, $"{(designer ? "Designer" : "Sensors")} depth={depth} width={width}");
            var name = Assert.Single(tree.GetVisualDescendants().OfType<TextBlock>(),
                t => ReferenceEquals(t.DataContext, leaf) && t.Text == leaf.DisplayName);
            Assert.Equal(leaf.DisplayName, ToolTip.GetTip(name));
            Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
            Assert.Equal(width < 830, name.TextLayout.TextLines.Any(line => line.HasCollapsed));
            // A changed live value must also remain inside the next layout.
            leaf.Value = leaf.Value == "12.3" ? "123.4" : "12.3";
        }
    });

    private void AssertValueInViewport(TreeView tree, SensorTreeItem sensor, string context)
    {
        var value = Assert.Single(tree.GetVisualDescendants().OfType<TextBlock>(),
            t => ReferenceEquals(t.DataContext, sensor) && t.Text == sensor.DisplayValue);
        var scroll = Assert.Single(tree.GetVisualDescendants().OfType<ScrollViewer>());
        var presenter = Assert.Single(scroll.GetVisualDescendants().OfType<ScrollContentPresenter>());
        var valueBounds = new Rect(value.Bounds.Size).TransformToAABB(value.TransformToVisual(tree)!.Value);
        var viewport = new Rect(scroll.Viewport).TransformToAABB(presenter.TransformToVisual(tree)!.Value);
        var details = $"{context}: {sensor.Name}, value={valueBounds}, viewport={viewport}, extent={scroll.Extent.Width:0.##}";
        output.WriteLine(details);
        Assert.True(valueBounds.Width > 0 && valueBounds.Height > 0, details);
        Assert.True(valueBounds.Left >= viewport.Left - 0.5 && valueBounds.Right <= viewport.Right + 0.5 &&
            valueBounds.Top >= viewport.Top - 0.5 && valueBounds.Bottom <= viewport.Bottom + 0.5, details);
        Assert.True(scroll.Extent.Width <= scroll.Viewport.Width + 0.5, details);
        Assert.True(value.TextLayout.Width <= value.Bounds.Width + 0.5, details);
    }
}
