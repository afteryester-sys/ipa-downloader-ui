using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace IPAStudio.App.Infrastructure;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can be refilled in a single operation,
/// raising one Reset notification instead of one notification per item.
///
/// This exists because the obvious way to bulk-fill a bound collection —
/// wrapping thousands of Add calls in <c>ICollectionView.DeferRefresh()</c> — is
/// actually illegal. Adding to the source makes the view adjust its Current position,
/// and touching Current while a refresh is deferred throws InvalidOperationException
/// ("Cannot change or check the contents or Current position of CollectionView while
/// Refresh is deferred"). A single Reset gives the same "one refresh, not thousands"
/// benefit without ever deferring.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>
    /// Replaces the entire contents, notifying listeners exactly once.
    /// </summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();

        // Mutate the inner list directly so no per-item events are raised.
        Items.Clear();
        foreach (var item in items) Items.Add(item);

        // Bound controls need both indexer and Count invalidated alongside the Reset,
        // otherwise things like item counts keep showing stale values.
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
