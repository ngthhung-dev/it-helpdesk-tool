using System.Diagnostics;
using System.Net.Http;

namespace ITHelpdeskTool.ViewModels
{
    /// <summary>Bản nhẹ chỉ có một trang — giữ chỗ cho tham số --page reinstall/download mà trang Cài lại sạch đọc.</summary>
    public static class ShellViewModel
    {
        public static string? StartPage { get; set; }
        public static string? StartSubPage => StartPage?.Split('/').ElementAtOrDefault(1);
    }
}

namespace ITHelpdeskTool
{
    /// <summary>
    /// Hàm có sẵn trên .NET 10 nhưng không có trên .NET Framework 4.8 — để mã nguồn dùng chung biên dịch được nguyên vẹn
    /// (hàm mở rộng chỉ được dùng khi kiểu không có hàm cùng tên).
    /// </summary>
    internal static class Net48Compat
    {
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> kv, out TKey key, out TValue value)
        {
            key = kv.Key;
            value = kv.Value;
        }

        public static string Replace(this string s, string oldValue, string? newValue, StringComparison comparison)
        {
            if (string.IsNullOrEmpty(oldValue)) return s;
            var sb = new System.Text.StringBuilder();
            int start = 0, i;
            while ((i = s.IndexOf(oldValue, start, comparison)) >= 0)
            {
                sb.Append(s, start, i - start).Append(newValue);
                start = i + oldValue.Length;
            }
            return sb.Append(s, start, s.Length - start).ToString();
        }

        public static bool Contains(this string s, string value, StringComparison comparison) => s.IndexOf(value, comparison) >= 0;

        public static Task WaitForExitAsync(this Process p, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => tcs.TrySetResult(true);
            if (p.HasExited) tcs.TrySetResult(true);
            if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public static Task<string> ReadAsStringAsync(this HttpContent c, CancellationToken ct) => c.ReadAsStringAsync();
    }
}
