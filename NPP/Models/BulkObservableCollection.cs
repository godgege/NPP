using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Netch.App.Models;

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(item);

        RaiseReset();
    }

    public void AddRange(IEnumerable<T> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            Items.Add(item);
            changed = true;
        }

        if (changed)
            RaiseReset();
    }

    private void RaiseReset()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
