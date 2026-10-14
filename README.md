# DeskMate — IT Helpdesk Toolkit

Công cụ hỗ trợ IT cho Windows 10/11, viết bằng **C# / .NET 10 / WPF**.

## Yêu cầu
- Windows 10 (19041+) hoặc Windows 11
- Visual Studio 2026 (workload *.NET desktop development*) hoặc .NET 10 SDK
- App yêu cầu quyền Administrator → chạy Visual Studio bằng "Run as administrator" để debug

## Build & chạy
```powershell
dotnet restore
dotnet run --project src/DeskMate
```

## Đóng gói
```powershell
# 1 file .exe tự chứa runtime (~70 MB, không cần cài .NET)
dotnet publish src/DeskMate -c Release -o publish
# Sau đó build installer/DeskMate.iss bằng Inno Setup 6.5 → dist/DeskMate-Setup-x.y.z.exe
```

## Cấu trúc
```
src/DeskMate/
├─ Core/          PageViewModel, NavItem, Converters
├─ Services/      SystemInfoService (Win32 + WMI), ProcessRunner (PowerShell/DISM/winget), AdminHelper
├─ ViewModels/    ShellViewModel (điều hướng), DashboardViewModel, PlaceholderViewModel
├─ Views/         DashboardPage, PlaceholderPage
├─ Controls/      RingGauge (vòng %), Sparkline (biểu đồ mini)
└─ Themes/        Colors.xaml (bảng màu tối), Controls.xaml (style nút, card, menu...)
```

## Thêm một trang mới
1. Tạo `XxxViewModel : PageViewModel` trong `ViewModels/`
2. Tạo `XxxPage.xaml` (UserControl) trong `Views/`
3. Thêm `DataTemplate` ánh xạ VM → View trong `App.xaml`
4. Trong `ShellViewModel`, gắn factory: `Item("key", "Tên", "", 1, () => new XxxViewModel())`

## Lộ trình
| Giai đoạn | Chức năng | Trạng thái |
|---|---|---|
| 1 | Dashboard | ✅ Xong |
| 1 | Startup & Boot, Tweaks, Dọn rác, App Store (winget) | ⏳ Tiếp theo |
| 2 | Báo cáo cấu hình, Policy, Power Plan, Gỡ app sạch, Font, App sau cài Win, Office, Sửa lỗi Windows | |
| 3 | Cài Windows, WinPE, USB boot, Tải ISO, BitLocker, Ổ đĩa, Chuyển thư mục, Máy in, Outlook, Xem trước, Multi-Zalo | |

## Build tự động (GitHub Actions)
- Mỗi lần push lên `main`: build + đóng gói, tải bản cài ở tab **Actions → run → Artifacts**.
- Tạo **Release**: vào **Actions → Build & Package → Run workflow**, nhập phiên bản (vd `0.2.0`). Hoặc push tag `v0.2.0`. Release kèm `DeskMate-Setup-x.y.z.exe` và bản portable.
