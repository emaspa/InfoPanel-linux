using InfoPanel.Designer;
using InfoPanel.Models;
using InfoPanel.Persistence;
using SkiaSharp;
using Xunit;

namespace InfoPanel.App.Tests
{
    /// <summary>
    /// Covers the designer's editing model (selection, gestures, undo/redo, clipboard,
    /// z-order) — the logic the canvas drives from pointer/keyboard input.
    /// </summary>
    public class DesignerSessionTests : IDisposable
    {
        private readonly string _tempDir;

        public DesignerSessionTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "infopanel-designer-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(_tempDir);
            ConfigPersistence.BaseFolderOverride = _tempDir;
        }

        public void Dispose()
        {
            ConfigPersistence.BaseFolderOverride = null;
            try { Directory.Delete(_tempDir, true); } catch { }
        }

        private static DesignerSession NewSession(params DisplayItem[] items)
        {
            var profile = new Profile { Guid = Guid.NewGuid(), Name = "Test", Width = 600, Height = 400 };
            var session = new DesignerSession(profile);
            foreach (var item in items)
            {
                item.SetProfile(profile);
                session.Items.Add(item);
            }

            return session;
        }

        private static ShapeDisplayItem Shape(int x, int y, int w = 50, int h = 40) =>
            new() { Name = $"shape-{x},{y}", X = x, Y = y, Width = w, Height = h };

        [Fact]
        public void HitTest_ReturnsTopmostItem()
        {
            var bottom = Shape(10, 10);
            var top = Shape(30, 20);
            var session = NewSession(bottom, top);

            // overlap region: both contain (35, 25); topmost (later in list) wins
            var hit = session.HitTest(new SKPoint(35, 25));

            Assert.Same(top, hit);
            Assert.Null(session.HitTest(new SKPoint(500, 300)));
        }

        [Fact]
        public void Select_SingleAndAdditiveToggle()
        {
            var a = Shape(0, 0);
            var b = Shape(100, 100);
            var session = NewSession(a, b);

            session.Select(a);
            Assert.True(a.Selected);
            Assert.Single(session.Selection);

            session.Select(b, additive: true);
            Assert.Equal(2, session.Selection.Count);

            session.Select(b, additive: true); // toggle off
            Assert.Single(session.Selection);
            Assert.False(b.Selected);

            session.Select(b); // exclusive
            Assert.Single(session.Selection);
            Assert.False(a.Selected);
            Assert.True(b.Selected);
        }

        [Fact]
        public void MarqueeSelection_SelectsIntersectingItems()
        {
            var a = Shape(10, 10);
            var b = Shape(200, 200);
            var c = Shape(400, 300);
            var session = NewSession(a, b, c);

            session.SelectInRect(new SKRect(0, 0, 260, 260));

            Assert.Equal(2, session.Selection.Count);
            Assert.Contains(a, session.Selection);
            Assert.Contains(b, session.Selection);
        }

        [Fact]
        public void MoveGesture_IsOneUndoStep_AndUndoRestoresGeometry()
        {
            var a = Shape(10, 10);
            var b = Shape(100, 100);
            var session = NewSession(a, b);
            session.Select(a);
            session.Select(b, additive: true);

            session.BeginGesture();
            session.MoveSelectionBy(25, 35);
            session.EndGesture("Move");

            Assert.Equal(35, a.X);
            Assert.Equal(45, a.Y);
            Assert.Equal(125, b.X);
            Assert.True(session.Undo.CanUndo);

            session.Undo.Undo();
            Assert.Equal(10, a.X);
            Assert.Equal(10, a.Y);
            Assert.Equal(100, b.X);

            session.Undo.Redo();
            Assert.Equal(35, a.X);
            Assert.Equal(135, b.Y);
        }

        [Fact]
        public void SnappedMove_SnapsPrimaryOriginToGrid()
        {
            var a = Shape(10, 10);
            var session = NewSession(a);
            session.Select(a);

            session.BeginGesture();
            session.MoveSelectionSnapped(23, 9, gridSpacing: 20);
            session.EndGesture("Move");

            // target (33, 19) snaps to (40, 20)
            Assert.Equal(40, a.X);
            Assert.Equal(20, a.Y);
        }

        [Fact]
        public void CancelGesture_RestoresGeometry()
        {
            var a = Shape(10, 10);
            var session = NewSession(a);
            session.Select(a);

            session.BeginGesture();
            session.MoveSelectionBy(50, 50);
            session.CancelGesture();

            Assert.Equal(10, a.X);
            Assert.Equal(10, a.Y);
            Assert.False(session.Undo.CanUndo);
        }

        [Fact]
        public void Delete_Undo_RestoresItemAtOriginalIndex()
        {
            var a = Shape(0, 0);
            var b = Shape(50, 50);
            var c = Shape(100, 100);
            var session = NewSession(a, b, c);

            session.Select(b);
            session.DeleteSelection();

            Assert.Equal([a, c], session.Items);

            session.Undo.Undo();
            Assert.Equal([a, b, c], session.Items);
        }

        [Fact]
        public void Duplicate_ClonesWithOffsetAndNewGuid()
        {
            var a = Shape(10, 10);
            var session = NewSession(a);
            session.Select(a);

            session.Duplicate();

            Assert.Equal(2, session.Items.Count);
            var clone = session.Items[1];
            Assert.NotEqual(a.Guid, clone.Guid);
            Assert.Equal(20, clone.X);
            Assert.Equal(20, clone.Y);
            Assert.Single(session.Selection);
            Assert.Same(clone, session.Selection[0]);

            session.Undo.Undo();
            Assert.Single(session.Items);
        }

        [Fact]
        public void Clipboard_CopyPaste_RoundTripsItems()
        {
            var a = Shape(10, 10);
            var session = NewSession(a);
            session.Select(a);

            var xml = session.CopySelectionToXml();
            Assert.NotNull(xml);
            Assert.Contains("ShapeDisplayItem", xml);

            Assert.True(session.PasteFromXml(xml!));
            Assert.Equal(2, session.Items.Count);
            var pasted = session.Items[1];
            Assert.NotEqual(a.Guid, pasted.Guid);
            Assert.Equal(20, pasted.X);
        }

        [Fact]
        public void ZOrder_PushByAndUndo()
        {
            var a = Shape(0, 0);
            var b = Shape(1, 1);
            var c = Shape(2, 2);
            var session = NewSession(a, b, c);

            session.Select(a);
            session.PushBy(1);
            Assert.Equal([b, a, c], session.Items);

            session.PushToEnd(front: true);
            Assert.Equal([b, c, a], session.Items);

            session.Undo.Undo();
            Assert.Equal([b, a, c], session.Items);
            session.Undo.Undo();
            Assert.Equal([a, b, c], session.Items);
        }

        [Fact]
        public void NudgeTwice_UndoRestoresEachStep()
        {
            var a = Shape(10, 10);
            var session = NewSession(a);
            session.Select(a);

            session.Nudge(1, 0);
            session.Nudge(0, 10);
            Assert.Equal(11, a.X);
            Assert.Equal(20, a.Y);

            session.Undo.Undo();
            Assert.Equal(10, a.Y);
            session.Undo.Undo();
            Assert.Equal(10, a.X);
        }

        [Fact]
        public void LockedItems_AreNotMoved()
        {
            var a = Shape(10, 10);
            a.IsLocked = true;
            var session = NewSession(a);
            session.Select(a);

            session.BeginGesture();
            session.MoveSelectionBy(50, 50);
            session.EndGesture("Move");

            Assert.Equal(10, a.X);
            Assert.False(session.Undo.CanUndo);
        }
    }
}
