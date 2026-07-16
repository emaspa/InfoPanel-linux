using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Stores;
using SkiaSharp;
using System.Collections.ObjectModel;
using System.Xml;
using System.Xml.Serialization;

namespace InfoPanel.Designer
{
    /// <summary>
    /// Editing state for one profile open in the designer: live item collection,
    /// selection, undo stack, and the edit operations the canvas/toolbar invoke.
    /// UI-thread only.
    /// </summary>
    public sealed class DesignerSession(Profile profile)
    {
        public Profile Profile { get; } = profile;
        public ObservableCollection<DisplayItem> Items { get; } = DisplayItemStore.Instance.GetOrLoad(profile);
        public ObservableCollection<DisplayItem> Selection { get; } = [];
        public UndoManager Undo { get; } = new();

        public event EventHandler? SelectionChanged;

        // ---- selection ----

        public void Select(DisplayItem item, bool additive = false)
        {
            if (!additive)
            {
                foreach (var selected in Selection.Where(s => s != item).ToList())
                {
                    selected.Selected = false;
                    Selection.Remove(selected);
                }
            }

            if (additive && Selection.Contains(item))
            {
                item.Selected = false;
                Selection.Remove(item);
            }
            else if (!Selection.Contains(item))
            {
                item.Selected = true;
                Selection.Add(item);
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ClearSelection()
        {
            foreach (var item in Selection)
            {
                item.Selected = false;
            }

            Selection.Clear();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SelectAll()
        {
            ClearSelection();
            foreach (var item in Items.Where(i => !i.Hidden))
            {
                item.Selected = true;
                Selection.Add(item);
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SelectInRect(SKRect worldRect)
        {
            ClearSelection();
            foreach (var item in Items.Where(i => !i.Hidden && i.EvaluateBounds().IntersectsWith(worldRect)))
            {
                item.Selected = true;
                Selection.Add(item);
            }

            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Topmost visible item containing the world point (reverse draw order, descending into groups).</summary>
        public DisplayItem? HitTest(SKPoint worldPoint)
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var item = Items[i];
                if (item.Hidden) continue;

                if (item is GroupDisplayItem group)
                {
                    for (int j = group.DisplayItems.Count - 1; j >= 0; j--)
                    {
                        var child = group.DisplayItems[j];
                        if (!child.Hidden && child.ContainsPoint(worldPoint))
                        {
                            return child;
                        }
                    }
                }
                else if (item.ContainsPoint(worldPoint))
                {
                    return item;
                }
            }

            return null;
        }

        // ---- gestures (move/resize recorded as one undo step) ----

        private List<(DisplayItem Item, (int X, int Y, int W, int H) Before)>? _gesture;

        public void BeginGesture()
        {
            _gesture = [.. Selection.Select(item =>
            {
                var (w, h) = ItemGeometry.GetSize(item);
                return (item, (item.X, item.Y, w, h));
            })];
        }

        public void EndGesture(string label)
        {
            if (_gesture == null) return;

            var changes = _gesture
                .Select(g =>
                {
                    var (w, h) = ItemGeometry.GetSize(g.Item);
                    return (g.Item, g.Before, After: (g.Item.X, g.Item.Y, w, h));
                })
                .Where(c => c.Before != c.After)
                .ToList();

            if (changes.Count > 0)
            {
                Undo.Record(new GeometryAction(label, changes));
            }

            _gesture = null;
        }

        public void CancelGesture()
        {
            if (_gesture == null) return;

            foreach (var (item, before) in _gesture)
            {
                item.X = before.X;
                item.Y = before.Y;
                ItemGeometry.SetSize(item, before.W, before.H);
            }

            _gesture = null;
        }

        public void MoveSelectionBy(int dx, int dy)
        {
            foreach (var item in Selection.Where(i => !i.IsLocked))
            {
                item.X += dx;
                item.Y += dy;
            }
        }

        /// <summary>Gesture-start geometry for an item in the active gesture, if any.</summary>
        public (int X, int Y, int W, int H)? GestureStartOf(DisplayItem item)
        {
            if (_gesture == null) return null;

            foreach (var (gestureItem, before) in _gesture)
            {
                if (gestureItem == item)
                {
                    return before;
                }
            }

            return null;
        }

        /// <summary>Restores gesture-start geometry without ending the gesture (used to re-apply absolute drag offsets).</summary>
        public void CancelGestureVisualOnly()
        {
            if (_gesture == null) return;

            foreach (var (item, before) in _gesture)
            {
                item.X = before.X;
                item.Y = before.Y;
                ItemGeometry.SetSize(item, before.W, before.H);
            }
        }

        /// <summary>Moves the selection by (dx,dy) from gesture start, snapping the primary item's origin to the grid.</summary>
        public void MoveSelectionSnapped(int dx, int dy, int gridSpacing)
        {
            if (_gesture == null || _gesture.Count == 0 || Selection.Count == 0)
            {
                MoveSelectionBy(dx, dy);
                return;
            }

            // snap the first selected item's new origin; move everything by the same snapped delta
            var primary = _gesture[0];
            var targetX = primary.Before.X + dx;
            var targetY = primary.Before.Y + dy;
            var snappedX = (int)Math.Round((double)targetX / gridSpacing) * gridSpacing;
            var snappedY = (int)Math.Round((double)targetY / gridSpacing) * gridSpacing;

            MoveSelectionBy(snappedX - primary.Before.X, snappedY - primary.Before.Y);
        }

        public void Nudge(int dx, int dy)
        {
            BeginGesture();
            MoveSelectionBy(dx, dy);
            EndGesture("Nudge");
        }

        // ---- edit operations ----

        public void DeleteSelection()
        {
            if (Selection.Count == 0) return;
            var action = new RemoveItemsAction(Items, [.. Selection]);
            ClearSelection();
            Undo.Execute(action);
        }

        public void AddItem(DisplayItem item)
        {
            item.SetProfile(Profile);
            Undo.Execute(new AddItemsAction(Items, [item]));
            Select(item);
        }

        public void Duplicate()
        {
            if (Selection.Count == 0) return;

            var clones = Selection
                .Select(item =>
                {
                    var clone = (DisplayItem)item.Clone();
                    clone.X += 10;
                    clone.Y += 10;
                    clone.SetProfile(Profile);
                    return clone;
                })
                .ToList();

            Undo.Execute(new AddItemsAction(Items, clones));

            ClearSelection();
            foreach (var clone in clones)
            {
                Select(clone, additive: true);
            }
        }

        // ---- z-order ----

        public void PushBy(int delta)
        {
            foreach (var item in (delta > 0 ? Selection.OrderByDescending(Items.IndexOf) : Selection.OrderBy(Items.IndexOf)).ToList())
            {
                var index = Items.IndexOf(item);
                if (index < 0) continue;
                var target = Math.Clamp(index + delta, 0, Items.Count - 1);
                if (target != index)
                {
                    Undo.Execute(new ReorderAction(Items, item, index, target));
                }
            }
        }

        public void PushToEnd(bool front)
        {
            foreach (var item in Selection.ToList())
            {
                var index = Items.IndexOf(item);
                if (index < 0) continue;
                var target = front ? Items.Count - 1 : 0;
                if (target != index)
                {
                    Undo.Execute(new ReorderAction(Items, item, index, target));
                }
            }
        }

        // ---- clipboard (XML, matching the profile file format) ----

        public string? CopySelectionToXml()
        {
            if (Selection.Count == 0) return null;

            var xs = new XmlSerializer(typeof(List<DisplayItem>), ConfigPersistence.DisplayItemExtraTypes);
            using var sw = new StringWriter();
            using (var wr = XmlWriter.Create(sw, new XmlWriterSettings { Indent = true }))
            {
                xs.Serialize(wr, Selection.ToList());
            }

            return sw.ToString();
        }

        public bool PasteFromXml(string xml)
        {
            try
            {
                var xs = new XmlSerializer(typeof(List<DisplayItem>), ConfigPersistence.DisplayItemExtraTypes);
                using var rd = XmlReader.Create(new StringReader(xml));
                if (xs.Deserialize(rd) is not List<DisplayItem> items || items.Count == 0)
                {
                    return false;
                }

                foreach (var item in items)
                {
                    item.Guid = Guid.NewGuid();
                    item.X += 10;
                    item.Y += 10;
                    item.SetProfile(Profile);
                }

                Undo.Execute(new AddItemsAction(Items, items));

                ClearSelection();
                foreach (var item in items)
                {
                    Select(item, additive: true);
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public void SaveNow() => DisplayItemStore.Instance.Save(Profile);
    }
}
