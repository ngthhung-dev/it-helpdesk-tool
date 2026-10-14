using System.IO;
using System.Runtime.InteropServices;

namespace ITHelpdeskTool.Services;

/// <summary>Thao tác file qua Shell: đưa vào Thùng rác, đọc/dọn Thùng rác.</summary>
public static class FileOps
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    /// <summary>Đưa file/thư mục vào Thùng rác (khôi phục được). Trả true nếu không còn trên đĩa.</summary>
    public static bool SendToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return true;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var r = SHFileOperation(ref op);
        return r == 0 && !op.fAnyOperationsAborted && !File.Exists(path) && !Directory.Exists(path);
    }

    public static (long Bytes, long Items) QueryRecycleBin()
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, info.i64NumItems) : (0, 0);
    }

    public static bool EmptyRecycleBin()
    {
        const uint noConfirm = 0x1, noProgress = 0x2, noSound = 0x4;
        var hr = SHEmptyRecycleBin(IntPtr.Zero, null, noConfirm | noProgress | noSound);
        // 0x8000FFFF (E_UNEXPECTED) khi thùng rác vốn đã trống
        return hr == 0 || QueryRecycleBin().Items == 0;
    }
}
