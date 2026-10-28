using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace DroidDeck.App.Services;

/// <summary>
/// An observable collection that can add, remove, and replace items in bulk while
/// raising a single reset notification, so high-rate log streaming does not flood
/// the UI with per-item change events.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    /// <summary>Adds many items and raises one reset notification.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var added = false;
        CheckReentrancy();
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }

        if (added)
        {
            RaiseReset();
        }
    }

    /// <summary>Removes the first <paramref name="count"/> items and raises one reset notification.</summary>
    public void RemoveFromStart(int count)
    {
        if (count <= 0 || Items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        for (var index = 0; index < count && Items.Count > 0; index++)
        {
            Items.RemoveAt(0);
        }

        RaiseReset();
    }

    /// <summary>Replaces all items and raises one reset notification.</summary>
    public void ResetAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
