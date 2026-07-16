using InfoPanel.Models;
using System.Collections.ObjectModel;

namespace InfoPanel.Designer
{
    public interface IUndoableAction
    {
        string Label { get; }
        void Do();
        void Undo();

        /// <summary>Try to absorb a subsequent action (e.g. successive edits of the same property).</summary>
        bool TryMerge(IUndoableAction next) => false;
    }

    /// <summary>Per-profile undo/redo stack. All actions run on the UI thread.</summary>
    public sealed class UndoManager
    {
        private readonly Stack<IUndoableAction> _undo = new();
        private readonly Stack<IUndoableAction> _redo = new();
        private const int MaxDepth = 200;

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        public event EventHandler? StateChanged;

        /// <summary>Executes the action and records it.</summary>
        public void Execute(IUndoableAction action)
        {
            action.Do();
            Record(action);
        }

        /// <summary>Records an action whose effect has already been applied (e.g. a completed drag gesture).</summary>
        public void Record(IUndoableAction action)
        {
            if (_undo.Count > 0 && _undo.Peek().TryMerge(action))
            {
                _redo.Clear();
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            _undo.Push(action);
            if (_undo.Count > MaxDepth)
            {
                var kept = _undo.Take(MaxDepth).Reverse().ToList();
                _undo.Clear();
                kept.ForEach(_undo.Push);
            }

            _redo.Clear();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            var action = _undo.Pop();
            action.Undo();
            _redo.Push(action);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            var action = _redo.Pop();
            action.Do();
            _undo.Push(action);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Move/resize gesture recorded once at gesture end: one undo step for the whole selection.</summary>
    public sealed class GeometryAction(string label, IReadOnlyList<(DisplayItem Item, (int X, int Y, int W, int H) Before, (int X, int Y, int W, int H) After)> changes) : IUndoableAction
    {
        public string Label => label;

        public void Do() => Apply(before: false);
        public void Undo() => Apply(before: true);

        private void Apply(bool before)
        {
            foreach (var (item, b, a) in changes)
            {
                var g = before ? b : a;
                item.X = g.X;
                item.Y = g.Y;
                ItemGeometry.SetSize(item, g.W, g.H);
            }
        }
    }

    public sealed class AddItemsAction(ObservableCollection<DisplayItem> collection, IReadOnlyList<DisplayItem> items) : IUndoableAction
    {
        public string Label => items.Count == 1 ? $"Add {items[0].Name}" : $"Add {items.Count} items";

        public void Do()
        {
            foreach (var item in items) collection.Add(item);
        }

        public void Undo()
        {
            foreach (var item in items) collection.Remove(item);
        }
    }

    public sealed class RemoveItemsAction : IUndoableAction
    {
        private readonly ObservableCollection<DisplayItem> _collection;
        private readonly List<(DisplayItem Item, int Index)> _removed;

        public RemoveItemsAction(ObservableCollection<DisplayItem> collection, IReadOnlyList<DisplayItem> items)
        {
            _collection = collection;
            _removed = [.. items
                .Select(item => (Item: item, Index: collection.IndexOf(item)))
                .Where(x => x.Index >= 0)
                .OrderBy(x => x.Index)];
        }

        public string Label => _removed.Count == 1 ? $"Delete {_removed[0].Item.Name}" : $"Delete {_removed.Count} items";

        public void Do()
        {
            foreach (var (item, _) in _removed)
            {
                _collection.Remove(item);
            }
        }

        public void Undo()
        {
            foreach (var (item, index) in _removed)
            {
                _collection.Insert(Math.Min(index, _collection.Count), item);
            }
        }
    }

    public sealed class ReorderAction(ObservableCollection<DisplayItem> collection, DisplayItem item, int fromIndex, int toIndex) : IUndoableAction
    {
        public string Label => "Reorder";

        public void Do() => collection.Move(collection.IndexOf(item), toIndex);
        public void Undo() => collection.Move(collection.IndexOf(item), fromIndex);
    }

    public sealed class SetPropertyAction<T>(DisplayItem item, string propertyName, Action<T> setter, T oldValue, T newValue) : IUndoableAction
    {
        public string Label => $"Change {propertyName}";

        public void Do() => setter(newValue);
        public void Undo() => setter(oldValue);

        public bool TryMerge(IUndoableAction next)
        {
            if (next is SetPropertyAction<T> other && ReferenceEquals(other.ItemRef, item) && other.PropertyRef == propertyName)
            {
                newValue = other.NewValueRef;
                setter(newValue);
                return true;
            }

            return false;
        }

        internal DisplayItem ItemRef => item;
        internal string PropertyRef => propertyName;
        internal T NewValueRef => newValue;
    }

    /// <summary>
    /// Per-item-type geometry policy: which items expose Width/Height, which scale, which are fixed.
    /// </summary>
    public static class ItemGeometry
    {
        public static (int W, int H) GetSize(DisplayItem item) => item switch
        {
            ChartDisplayItem chart => (chart.Width, chart.Height),
            TextDisplayItem text => (text.Width, text.Height),
            ImageDisplayItem image => (image.Width, image.Height),
            ShapeDisplayItem shape => (shape.Width, shape.Height),
            _ => (0, 0)
        };

        public static void SetSize(DisplayItem item, int w, int h)
        {
            switch (item)
            {
                case ChartDisplayItem chart:
                    chart.Width = w;
                    chart.Height = h;
                    break;
                case TextDisplayItem text:
                    text.Width = w;
                    text.Height = h;
                    break;
                case ImageDisplayItem image:
                    image.Width = w;
                    image.Height = h;
                    break;
                case ShapeDisplayItem shape:
                    shape.Width = w;
                    shape.Height = h;
                    break;
            }
        }

        public static bool IsResizable(DisplayItem item) =>
            item is ChartDisplayItem or TextDisplayItem or ImageDisplayItem or ShapeDisplayItem;
    }
}
