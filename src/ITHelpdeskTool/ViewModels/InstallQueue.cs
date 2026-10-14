using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.SysConfig;

namespace ITHelpdeskTool.ViewModels;

public enum QueueState { Pending, Running, Done, Failed, Cancelled }

public sealed partial class QueueItem(string key, string name, Func<Action<string>, CancellationToken, Task<OpResult>> work) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    public Func<Action<string>, CancellationToken, Task<OpResult>> Work { get; } = work;
    [ObservableProperty] private QueueState _state;
    [ObservableProperty] private string _status = "Đang chờ";
    public OpResult? Result { get; set; }
}

/// <summary>Hàng đợi cài đặt: chạy lần lượt từng mục (bộ cài Windows không chạy song song được).</summary>
public sealed partial class InstallQueue : ObservableObject
{
    public ObservableCollection<QueueItem> Items { get; } = [];
    private CancellationTokenSource? _cts;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private bool _isRunning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private int _doneCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private int _failCount;
    public bool NeedsRestart { get; private set; }

    /// <summary>Gọi sau mỗi mục xong (để trang cập nhật trạng thái "đã cài").</summary>
    public event Action<QueueItem>? ItemFinished;
    public event Action? Finished;

    public string Summary
    {
        get
        {
            var total = Items.Count;
            if (total == 0) return "";
            var pending = Items.Count(i => i.State is QueueState.Pending or QueueState.Running);
            return IsRunning
                ? $"Đang cài {total - pending + 1}/{total} · xong {DoneCount}" + (FailCount > 0 ? $" · lỗi {FailCount}" : "")
                : $"Hoàn tất: {DoneCount} thành công" + (FailCount > 0 ? $", {FailCount} lỗi" : "") + (NeedsRestart ? " · nên khởi động lại máy" : "");
        }
    }

    public bool Contains(string key) => Items.Any(i => i.Key == key && i.State is QueueState.Pending or QueueState.Running);

    public void Add(string key, string name, Func<Action<string>, CancellationToken, Task<OpResult>> work)
    {
        if (Contains(key)) return;
        if (!IsRunning) ClearFinished();
        Items.Add(new QueueItem(key, name, work));
        OnPropertyChanged(nameof(Summary));
        if (!IsRunning) _ = RunAsync();
    }

    private void ClearFinished()
    {
        foreach (var i in Items.Where(i => i.State is not (QueueState.Pending or QueueState.Running)).ToList()) Items.Remove(i);
        DoneCount = FailCount = 0;
        NeedsRestart = false;
    }

    private async Task RunAsync()
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var dispatcher = System.Windows.Application.Current.Dispatcher;
        try
        {
            while (Items.FirstOrDefault(i => i.State == QueueState.Pending) is { } item)
            {
                item.State = QueueState.Running;
                item.Status = "Đang bắt đầu…";
                OnPropertyChanged(nameof(Summary));
                OpResult r;
                try
                {
                    var ct = _cts.Token;
                    r = await Task.Run(() => item.Work(s => dispatcher.BeginInvoke(() => item.Status = s), ct), ct);
                }
                catch (OperationCanceledException) { r = OpResult.Fail("Đã huỷ."); item.State = QueueState.Cancelled; }
                catch (Exception ex) { r = OpResult.Fail(ex.Message); }
                item.Result = r;
                if (item.State != QueueState.Cancelled) item.State = r.Ok ? QueueState.Done : QueueState.Failed;
                item.Status = r.Message;
                if (r.Ok) DoneCount++; else FailCount++;
                if (r.NeedsRestart) NeedsRestart = true;
                if (r.Ok) Log.Info($"Cài: {r.Message}"); else Log.Warn($"Cài: {r.Message}");
                ItemFinished?.Invoke(item);
                if (_cts.IsCancellationRequested)
                    foreach (var p in Items.Where(i => i.State == QueueState.Pending)) { p.State = QueueState.Cancelled; p.Status = "Đã huỷ"; }
            }
        }
        finally
        {
            IsRunning = false;
            OnPropertyChanged(nameof(Summary));
            Finished?.Invoke();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        if (!IsRunning) { Items.Clear(); DoneCount = FailCount = 0; OnPropertyChanged(nameof(Summary)); }
    }
}
