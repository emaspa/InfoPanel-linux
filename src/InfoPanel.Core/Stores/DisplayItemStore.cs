using InfoPanel.Models;
using InfoPanel.Persistence;
using InfoPanel.Utils;
using Serilog;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace InfoPanel.Stores
{
    /// <summary>
    /// Owns the live display-item collections per profile: loads them on demand,
    /// keeps an immutable snapshot for render consumers (lock-free reads from the
    /// render/device threads), and autosaves with a debounce after edits.
    /// Replaces the item-management half of v1's SharedModel.
    /// </summary>
    public sealed class DisplayItemStore
    {
        private static readonly ILogger Logger = Log.ForContext<DisplayItemStore>();
        private static readonly Lazy<DisplayItemStore> _instance = new(() => new DisplayItemStore());
        public static DisplayItemStore Instance => _instance.Value;

        private readonly ConcurrentDictionary<Guid, ObservableCollection<DisplayItem>> _items = new();
        private readonly ConcurrentDictionary<Guid, ImmutableList<DisplayItem>> _snapshots = new();
        private readonly ConcurrentDictionary<Guid, Debouncer> _saveDebouncers = new();
        private readonly ConcurrentDictionary<Guid, Profile> _profiles = new();
        private readonly Lock _loadLock = new();

        private DisplayItemStore() { }

        /// <summary>Live editable collection for a profile (loads from disk on first access; UI thread only).</summary>
        public ObservableCollection<DisplayItem> GetOrLoad(Profile profile)
        {
            if (_items.TryGetValue(profile.Guid, out var existing))
            {
                return existing;
            }

            lock (_loadLock)
            {
                if (_items.TryGetValue(profile.Guid, out existing))
                {
                    return existing;
                }

                var loaded = ConfigPersistence.LoadDisplayItems(profile);
                var collection = new ObservableCollection<DisplayItem>(loaded);
                _profiles[profile.Guid] = profile;
                _snapshots[profile.Guid] = [.. collection];

                collection.CollectionChanged += (_, e) => OnCollectionChanged(profile, collection, e);
                foreach (var item in collection)
                {
                    HookItem(profile, item);
                }

                _items[profile.Guid] = collection;
                return collection;
            }
        }

        /// <summary>Lock-free snapshot for renderers. Falls back to loading if never accessed.</summary>
        public ImmutableList<DisplayItem> GetSnapshot(Profile profile)
        {
            if (_snapshots.TryGetValue(profile.Guid, out var snapshot))
            {
                return snapshot;
            }

            // Renderers can hit a profile before any UI does; load outside the UI thread is fine
            // because the collection is only mutated by the UI afterwards.
            GetOrLoad(profile);
            return _snapshots.TryGetValue(profile.Guid, out snapshot) ? snapshot : [];
        }

        private void OnCollectionChanged(Profile profile, ObservableCollection<DisplayItem> collection, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (DisplayItem item in e.NewItems)
                {
                    item.SetProfile(profile);
                    HookItem(profile, item);
                }
            }

            RefreshSnapshot(profile, collection);
            RequestSave(profile);
        }

        private void HookItem(Profile profile, DisplayItem item)
        {
            item.PropertyChanged += (_, args) =>
            {
                // Selection is a UI-only, non-persisted property; don't autosave on it
                if (args.PropertyName == nameof(DisplayItem.Selected) || args.PropertyName == nameof(DisplayItem.MouseOffset))
                {
                    return;
                }

                RequestSave(profile);
            };

            if (item is GroupDisplayItem group)
            {
                group.DisplayItems.CollectionChanged += (_, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (DisplayItem child in e.NewItems)
                        {
                            child.SetProfile(profile);
                            HookItem(profile, child);
                        }
                    }

                    if (_items.TryGetValue(profile.Guid, out var collection))
                    {
                        RefreshSnapshot(profile, collection);
                    }

                    RequestSave(profile);
                };
            }
        }

        private void RefreshSnapshot(Profile profile, ObservableCollection<DisplayItem> collection)
        {
            _snapshots[profile.Guid] = [.. collection];
        }

        /// <summary>Schedules a debounced save (~2s after the last edit).</summary>
        public void RequestSave(Profile profile)
        {
            var debouncer = _saveDebouncers.GetOrAdd(profile.Guid, _ => new Debouncer());
            debouncer.Debounce(() => Save(profile), 2000);
        }

        /// <summary>Discards in-memory items and reloads the profile's collection from disk.</summary>
        public void Reload(Profile profile)
        {
            if (_items.TryGetValue(profile.Guid, out var collection))
            {
                var loaded = ConfigPersistence.LoadDisplayItems(profile);
                collection.Clear();
                foreach (var item in loaded)
                {
                    collection.Add(item);
                }
            }
        }

        /// <summary>Saves immediately (Ctrl+S, profile switch, shutdown).</summary>
        public void Save(Profile profile)
        {
            try
            {
                if (_snapshots.TryGetValue(profile.Guid, out var snapshot))
                {
                    ConfigPersistence.SaveDisplayItems(profile, snapshot);
                    Logger.Debug("Saved {Count} display items for {Profile}", snapshot.Count, profile.Name);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to save display items for {Profile}", profile.Name);
            }
        }

        public void SaveAll()
        {
            foreach (var guid in _snapshots.Keys)
            {
                if (_profiles.TryGetValue(guid, out var profile))
                {
                    Save(profile);
                }
            }
        }
    }
}
