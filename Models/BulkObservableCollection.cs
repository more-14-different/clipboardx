using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace ClipboardManager.Models;

/// <summary>
/// 支持批量更新的 ObservableCollection：在 BeginBulkUpdate/EndBulkUpdate 期间抑制逐项通知，
/// 结束时一次性发出 Reset 通知，避免大量 Add 触发频繁 UI 布局。
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotification;

    public BulkDisposable BeginBulkUpdate()
    {
        _suppressNotification = true;
        return new BulkDisposable(this);
    }

    private void EndBulkUpdate()
    {
        _suppressNotification = false;
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (_suppressNotification) return;
        base.OnCollectionChanged(e);
    }

    public readonly struct BulkDisposable : IDisposable
    {
        private readonly BulkObservableCollection<T> _collection;
        public BulkDisposable(BulkObservableCollection<T> collection) => _collection = collection;
        public void Dispose() => _collection.EndBulkUpdate();
    }
}
