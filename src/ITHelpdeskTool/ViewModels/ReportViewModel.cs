using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ITHelpdeskTool.Core;
using ITHelpdeskTool.Services;
using ITHelpdeskTool.Services.Report;
using Microsoft.Win32;

namespace ITHelpdeskTool.ViewModels;

public sealed partial class ReportViewModel : PageViewModel
{
    public override string Title => "Báo cáo cấu hình";

    public ObservableCollection<ReportSection> Sections { get; } = new(ReportCollector.CreateSections());
    public ObservableCollection<ReportIssue> Issues { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(ExportCommand), nameof(CopyTextCommand))]
    private bool _isScanning;

    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private string _statusText = "Chưa quét";
    [ObservableProperty] private string _toast = "";
    [ObservableProperty] private string? _lastExportPath;

    private DateTime _scannedAt;
    private TimeSpan _elapsed;

    public int BadCount => Issues.Count(i => i.Tone == Tone.Bad);
    public int WarnCount => Issues.Count(i => i.Tone == Tone.Warn);

    public override void OnNavigatedTo()
    {
        if (!HasScanned && !IsScanning) ScanCommand.Execute(null);
    }

    private bool CanAct() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ScanAsync()
    {
        IsScanning = true;
        Issues.Clear();
        OnPropertyChanged(nameof(BadCount));
        OnPropertyChanged(nameof(WarnCount));

        var done = 0;
        StatusText = $"Đang quét 0/{Sections.Count} mục…";
        var sw = Stopwatch.StartNew();
        try
        {
            await ReportCollector.RunAsync(Sections, () =>
                StatusText = $"Đang quét {Interlocked.Increment(ref done)}/{Sections.Count} mục…");
        }
        finally
        {
            _elapsed = sw.Elapsed;
            _scannedAt = DateTime.Now;
            foreach (var i in ReportCollector.FindIssues(Sections)) Issues.Add(i);
            OnPropertyChanged(nameof(BadCount));
            OnPropertyChanged(nameof(WarnCount));
            StatusText = $"Quét lúc {_scannedAt:HH:mm dd/MM/yyyy} · mất {_elapsed.TotalSeconds:0.0} giây";
            HasScanned = true;
            IsScanning = false;
        }
    }

    private ReportExporter.Meta Meta => new(_scannedAt, _elapsed);

    /// <summary>Xuất báo cáo; tham số là "html", "txt" hoặc "json".</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Export(string kind)
    {
        if (!HasScanned) return;
        var (filter, ext) = kind switch
        {
            "txt" => ("Văn bản (*.txt)|*.txt", ".txt"),
            "json" => ("JSON (*.json)|*.json", ".json"),
            _ => ("Trang web HTML – mở bằng trình duyệt, in ra PDF được (*.html)|*.html", ".html"),
        };

        var dlg = new SaveFileDialog
        {
            Title = "Lưu báo cáo cấu hình",
            Filter = filter,
            FileName = ReportExporter.SuggestedFileName(_scannedAt) + ext,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var content = ext switch
            {
                ".txt" => ReportExporter.ToText(Sections, Issues, Meta),
                ".json" => ReportExporter.ToJson(Sections, Issues, Meta),
                _ => ReportExporter.ToHtml(Sections, Issues, Meta),
            };
            ReportExporter.Save(dlg.FileName, content);
            LastExportPath = dlg.FileName;
            Log.Info($"Đã xuất báo cáo: {dlg.FileName}");
            ShowToast($"Đã lưu {Path.GetFileName(dlg.FileName)}");
            if (ext == ".html") ShellLauncher.Open(dlg.FileName);   // mở luôn trong trình duyệt
        }
        catch (Exception ex)
        {
            Log.Error("Xuất báo cáo lỗi", ex);
            MessageBox.Show($"Không lưu được file:\n{ex.Message}", AppInfo.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void CopyText()
    {
        if (!HasScanned) return;
        try
        {
            Clipboard.SetText(ReportExporter.ToText(Sections, Issues, Meta));
            ShowToast("Đã chép báo cáo dạng văn bản");
        }
        catch { ShowToast("Clipboard đang bận, thử lại"); }
    }

    [RelayCommand]
    private void OpenExportFolder()
    {
        if (LastExportPath is null || !File.Exists(LastExportPath)) return;
        ShellLauncher.Open("explorer.exe", $"/select,\"{LastExportPath}\"");
    }

    private async void ShowToast(string text)
    {
        Toast = text;
        await Task.Delay(3000);
        if (Toast == text) Toast = "";
    }
}
