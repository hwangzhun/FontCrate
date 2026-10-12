using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FontCrate.App;

public sealed class ResettableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> values)
    {
        var snapshot = values.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var value in snapshot) Items.Add(value);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
