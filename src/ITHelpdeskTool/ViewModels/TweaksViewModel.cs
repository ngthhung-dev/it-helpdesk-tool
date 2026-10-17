using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.SysConfig;
using ITHelpdeskTool.Services.Tweaks;
using ITHelpdeskTool.Views;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class TweakItem(TweakDef def) : ObservableObject
{
    public TweakDef Def { get; } = def;
    public string Title => Def.Title;
    public string Description => Def.Description;
    public bool HasDescription => Def.Description.Length > 0;
    public string? Warning => Def.Warning;
    public bool Recommended => Def.Recommended;
    public bool IsAvailable { get; } = TweakService.IsAvailable(def);
    public string? UnavailableText => IsAvailable ? null : Def.Win11Only && !TweakService.IsWin11 ? "Chỉ có trên Windows 11" : "Máy này không có dịch vụ tương ứng";
    public string? EffectText => Def.Effect switch
    {
        TweakEffect.Explorer => "Cần khởi động lại Explorer",
        TweakEffect.Relogon => "Cần đăng xuất lại",
        TweakEffect.Restart => "Cần khởi động lại máy",
        _ => null,
    };

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private bool _isVisible = true;

    public void SetState(bool on)
    {
        if (IsOn == on) OnPropertyChanged(nameof(IsOn)); else IsOn = on;
    }
}

public sealed partial class TweakGroup(string title, string glyph, IReadOnlyList<TweakItem> items) : ObservableObject
{
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public IReadOnlyList<TweakItem> Items { get; } = items;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private int _onCount;
}

public sealed partial class TweaksViewModel : PageViewModel
{
    public override string Title => "Tweaks Windows";
    private bool _loaded;

    public IReadOnlyList<TweakGroup> Groups { get; }
    public ObservableCollection<ActionLogEntry> ActionLog { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private string _busyText = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _needsRestart;
    [ObservableProperty] private bool _needsExplorer;

    private static readonly Dictionary<string, string> Glyphs = new()
    {
        [TweakCatalog.GroupUi] = "\uE7F4",
        [TweakCatalog.GroupPerf] = "\uEC4A",
        [TweakCatalog.GroupPrivacy] = "\uE72E",
        [TweakCatalog.GroupUtil] = "\uE90F",
    };

    /// <summary>Xếp A→Z theo tiếng Việt (Ẩn, Bật, Chế độ, Hiện, Mở…).</summary>
    private static readonly StringComparer ViOrder = StringComparer.Create(new System.Globalization.CultureInfo("vi-VN"), true);

    public TweaksViewModel()
    {
        Groups = TweakCatalog.Groups
            .Select(g => new TweakGroup(g, Glyphs.GetValueOrDefault(g, "\uE713"),
                TweakCatalog.All.Where(t => t.Group == g)
                    .OrderBy(t => string.Concat(t.Title.SkipWhile(c => !char.IsLetterOrDigit(c))), ViOrder)
                    .Select(t => new TweakItem(t)).ToList()))
            .ToList();
    }

    private IEnumerable<TweakItem> AllItems => Groups.SelectMany(g => g.Items);

    public override void OnNavigatedTo()
    {
        if (_loaded) return;
        _loaded = true;
        _ = LoadAsync();
    }

    private void AddLog(string text, bool ok)
    {
        ActionLog.Insert(0, new ActionLogEntry(DateTime.Now.ToString("HH:mm:ss"), text, ok));
        while (ActionLog.Count > 50) ActionLog.RemoveAt(ActionLog.Count - 1);
        if (ok) Log.Info($"Tweaks: {text}"); else Log.Warn($"Tweaks: {text}");
    }

    private async Task Run(string busy, Func<Task<OpResult>> op, bool explorer = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = busy + "…";
        OpResult r;
        try { r = await op(); }
        catch (Exception ex) { r = OpResult.Fail(ex.Message); }
        finally { IsBusy = false; }
        AddLog(r.Message, r.Ok);
        if (r.NeedsRestart) NeedsRestart = true;
        if (r.Ok && explorer) NeedsExplorer = true;
        if (!r.Ok) DialogWindow.Info("Chưa thực hiện được", r.Message, error: true);
        await LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var states = await Task.Run(TweakService.ReadAll);
        foreach (var i in AllItems) i.SetState(states.GetValueOrDefault(i.Def.Key));
        foreach (var g in Groups) g.OnCount = g.Items.Count(i => i.IsOn);
        var avail = AllItems.Count(i => i.IsAvailable);
        var rec = AllItems.Where(i => i.IsAvailable && i.Recommended).ToList();
        Summary = $"Đang bật {AllItems.Count(i => i.IsOn)}/{avail} tweak · mục khuyên dùng: {rec.Count(i => i.IsOn)}/{rec.Count}";
        RecommendedTip = "Mục khuyên dùng (✓ = đang bật):\n" + string.Join("\n", rec.Select(i => (i.IsOn ? "✓ " : "• ") + i.Title));
    }

    [ObservableProperty] private string _recommendedTip = "";

    partial void OnFilterChanged(string value)
    {
        var f = value.Trim();
        foreach (var g in Groups)
        {
            foreach (var i in g.Items)
                i.IsVisible = f.Length == 0 || i.Title.Contains(f, StringComparison.CurrentCultureIgnoreCase)
                              || i.Description.Contains(f, StringComparison.CurrentCultureIgnoreCase);
            g.IsVisible = g.Items.Any(i => i.IsVisible);
        }
    }

    [RelayCommand]
    private Task ToggleAsync(TweakItem item)
    {
        var on = item.IsOn;
        if (on && item.Warning is { } w && !DialogWindow.Confirm(item.Title, w, "Vẫn bật"))
        {
            item.SetState(false);
            return Task.CompletedTask;
        }
        return Run(on ? $"Đang bật {item.Title}" : $"Đang tắt {item.Title}",
            () => TweakService.ApplyAsync(item.Def, on), item.Def.Effect == TweakEffect.Explorer);
    }

    [RelayCommand]
    private Task ApplyRecommendedAsync()
    {
        var list = AllItems.Where(i => i.IsAvailable && i.Recommended && !i.IsOn).ToList();
        if (list.Count == 0) { DialogWindow.Info("Đã đủ", "Mọi mục khuyên dùng đều đang bật."); return Task.CompletedTask; }
        if (!DialogWindow.Confirm("Bật các mục khuyên dùng?",
                "Sẽ bật:\n• " + string.Join("\n• ", list.Select(i => i.Title)) + "\n\nĐây là các thay đổi an toàn, tắt lại được bất cứ lúc nào.", "Bật"))
            return Task.CompletedTask;
        return Run("Đang áp dụng mục khuyên dùng", () => TweakService.ApplyManyAsync(list.Select(i => (i.Def, true))), explorer: true);
    }

    [RelayCommand]
    private Task ResetAllAsync()
    {
        var list = AllItems.Where(i => i.IsOn).ToList();
        if (list.Count == 0) { DialogWindow.Info("Không có gì để hoàn tác", "Chưa bật tweak nào."); return Task.CompletedTask; }
        if (!DialogWindow.Confirm("Trả tất cả về mặc định?", $"Tắt {list.Count} tweak đang bật, trả về thiết lập gốc của Windows.", "Trả về mặc định", danger: true))
            return Task.CompletedTask;
        return Run("Đang trả về mặc định", () => TweakService.ApplyManyAsync(list.Select(i => (i.Def, false))), explorer: true);
    }

    [RelayCommand]
    private async Task RestartExplorerAsync()
    {
        await Run("Đang khởi động lại Explorer", TweakService.RestartExplorerAsync);
        NeedsExplorer = false;
    }

    [RelayCommand]
    private void Restart()
    {
        if (!DialogWindow.Confirm("Khởi động lại máy?", "Lưu lại công việc đang mở. Máy sẽ khởi động lại sau 10 giây.", "Khởi động lại", danger: true))
            return;
        var r = SysConfigService.RestartComputer();
        AddLog(r.Message, r.Ok);
    }
}
