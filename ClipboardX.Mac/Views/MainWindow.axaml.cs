using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ClipboardX.Core;

namespace ClipboardX.Mac.Views;

public partial class MainWindow : Window
{
    private readonly ClipboardHistorySession _session;
    private readonly MacSettings _settings;
    private readonly DispatcherTimer _pollTimer;
    private readonly PasteSimulator _pasteSim = new();
    private readonly ObservableCollection<HistoryEntry> _filtered = new();

    public MainWindow(ClipboardHistorySession session, MacSettings settings)
    {
        InitializeComponent();
        _session = session;
        _settings = settings;

        HistoryList.ItemsSource = _filtered;

        SearchBox.TextChanged += (_, _) => RefreshFilter();
        ((INotifyCollectionChanged)_session.Items).CollectionChanged +=
            (_, _) => Dispatcher.UIThread.Post(RefreshFilter);

        PasteButton.Click += async (_, _) => await PasteSelectedAsync();
        RemoveButton.Click += (_, _) => RemoveSelected();
        ClearButton.Click += (_, _) => ClearAll();

        HistoryList.DoubleTapped += async (_, _) => await PasteSelectedAsync();

        KeyDown += OnWindowKeyDown;

        Closing += (_, e) =>
        {
            e.Cancel = true;
            Hide();
        };

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(settings.PollIntervalMs, 200, 2000)) };
        _pollTimer.Tick += async (_, _) => await PollClipboardAsync();
        _pollTimer.Start();

        RefreshFilter();
        UpdateStatus();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && HistoryList.SelectedItem is HistoryEntry)
        {
            _ = PasteSelectedAsync();
            e.Handled = true;
        }
    }

    public void ToggleVisible()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsVisible)
                Hide();
            else
                ShowFromTray();
        });
    }

    public void ShowFromTray()
    {
        Show();
        Activate();
        Focus();
        RefreshFilter();
        UpdateStatus();
    }

    private void RefreshFilter()
    {
        var q = SearchBox.Text?.Trim() ?? "";
        _filtered.Clear();
        foreach (var x in _session.Items)
        {
            if (x.MatchesSearch(q))
                _filtered.Add(x);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusBar.Text = $"{_session.Items.Count} 条历史 · 筛选后 {_filtered.Count} 条";
    }

    private async Task PollClipboardAsync()
    {
        try
        {
            if (_session.IgnorePollTicks > 0)
            {
                _session.IgnorePollTicks--;
                return;
            }

            var clipboard = Clipboard;
            if (clipboard is null) return;

            var entry = await ClipboardCapture.TryCaptureAsync(clipboard);
            if (entry == null) return;

            if (!_session.TryRecordCaptured(entry))
                return;

            RefreshFilter();
        }
        catch
        {
            /* ignore */
        }
    }

    private async Task PasteSelectedAsync()
    {
        if (HistoryList.SelectedItem is not HistoryEntry entry)
            return;

        var clipboard = Clipboard;
        var storageProvider = StorageProvider;
        if (clipboard == null || storageProvider == null)
            return;

        _session.TouchReuse(entry);
        _session.IgnorePollTicks = 5;

        try
        {
            switch (entry.Type)
            {
                case EntryType.Text:
                    await clipboard.SetTextAsync(entry.TextContent ?? "");
                    break;
                case EntryType.Files when entry.FilePaths is { Length: > 0 }:
                {
                    var files = new List<IStorageFile>();
                    foreach (var p in entry.FilePaths)
                    {
                        var f = await storageProvider.TryGetFileFromPathAsync(p);
                        if (f != null)
                            files.Add(f);
                    }
                    if (files.Count > 0)
                    {
                        var dob = new DataObject();
                        dob.Set(DataFormats.Files, files);
                        await clipboard.SetDataObjectAsync(dob);
                    }
                    break;
                }
                case EntryType.Image when entry.ImageData is { Length: > 0 }:
                {
                    var dob = new DataObject();
                    dob.Set("PNG", entry.ImageData);
                    dob.Set("public.png", entry.ImageData);
                    await clipboard.SetDataObjectAsync(dob);
                    break;
                }
            }
        }
        catch
        {
            /* ignore */
        }

        Hide();
        await Task.Delay(90);
        _pasteSim.PasteCmdV();
        RefreshFilter();
    }

    private void RemoveSelected()
    {
        if (HistoryList.SelectedItem is not HistoryEntry entry)
            return;
        _session.Remove(entry);
        RefreshFilter();
    }

    private void ClearAll()
    {
        _session.ClearAll();
        RefreshFilter();
    }
}
