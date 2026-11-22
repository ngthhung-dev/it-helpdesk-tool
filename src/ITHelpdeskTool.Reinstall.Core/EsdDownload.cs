using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ITHelpdeskTool.Reinstall
{
    /// <summary>Một file bộ cài .esd gốc của Microsoft (theo danh mục của Media Creation Tool).</summary>
    public sealed class EsdFile
    {
        public string Product { get; set; } = "";
        public string FileName { get; set; } = "";
        public string LanguageCode { get; set; } = "";
        public string Language { get; set; } = "";
        public string Architecture { get; set; } = "";
        public long Size { get; set; }
        public string Sha1 { get; set; } = "";
        public string Url { get; set; } = "";
        public List<string> Editions { get; set; } = new List<string>();

        /// <summary>Số build ở đầu tên file, vd 26100.4349.</summary>
        public string Build => Regex.Match(FileName, @"^\d+\.\d+").Value;

        /// <summary>Bộ cài bán lẻ (Home / Pro / Education…), không phải bản doanh nghiệp (Enterprise, cần KMS).</summary>
        public bool Consumer => Editions.Any(e => e.Equals("Professional", StringComparison.OrdinalIgnoreCase) || e.Equals("Core", StringComparison.OrdinalIgnoreCase)
                                                 || e.Equals("CoreCountrySpecific", StringComparison.OrdinalIgnoreCase));

        public override string ToString() => $"{Product} {Build} · {Language} · {Architecture} · {Size / 1073741824.0:0.0} GB";
    }

    /// <summary>Danh mục bộ cài chính thức (products.cab) mà Media Creation Tool của Microsoft dùng.</summary>
    public static class EsdCatalog
    {
        public const string Windows11 = "Windows 11";
        public const string Windows10 = "Windows 10";

        /// <summary>Link danh mục của Media Creation Tool (Microsoft cập nhật khi có bản mới).</summary>
        public static string CatalogUrl(string product) => product == Windows10
            ? "https://go.microsoft.com/fwlink/?LinkId=841361"
            : "https://go.microsoft.com/fwlink/?LinkId=2156292";

        /// <summary>Đọc products.xml: gộp các dòng cùng file (mỗi phiên bản một dòng), chỉ giữ link của Microsoft.</summary>
        public static List<EsdFile> Parse(string xml, string product)
        {
            var doc = XDocument.Parse(xml);
            var files = new Dictionary<string, EsdFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.Descendants("File"))
            {
                string V(string n) => (string?)f.Element(n) ?? "";
                var name = V("FileName");
                var url = V("FilePath");
                if (name.Length == 0 || !name.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) || !IsMicrosoftUrl(url)) continue;
                if (!files.TryGetValue(name, out var e))
                {
                    long.TryParse(V("Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);
                    e = new EsdFile
                    {
                        Product = product, FileName = name, LanguageCode = V("LanguageCode").ToLowerInvariant(), Language = V("Language"),
                        Architecture = V("Architecture"), Size = size, Sha1 = V("Sha1").ToLowerInvariant(), Url = url,
                    };
                    files[name] = e;
                }
                var ed = V("Edition");
                if (ed.Length > 0 && !e.Editions.Contains(ed)) e.Editions.Add(ed);
            }
            return files.Values.OrderBy(x => x.Language).ThenBy(x => x.Architecture).ToList();
        }

        /// <summary>Chỉ nhận link tải trên máy chủ của Microsoft.</summary>
        public static bool IsMicrosoftUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
            if (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps) return false;
            var h = u.Host.ToLowerInvariant();
            return h == "microsoft.com" || h.EndsWith(".microsoft.com", StringComparison.Ordinal);
        }

        /// <summary>Tải products.cab, giải nén products.xml (expand.exe có sẵn trên Windows) rồi đọc.</summary>
        public static async Task<List<EsdFile>> LoadAsync(IRunner run, string product, string workDir, CancellationToken ct = default)
        {
            Directory.CreateDirectory(workDir);
            var cab = Path.Combine(workDir, "products.cab");
            var xml = Path.Combine(workDir, "products.xml");
            foreach (var f in new[] { cab, xml }) if (File.Exists(f)) File.Delete(f);
            using (var http = EsdDownloader.Client())
            using (var resp = await http.GetAsync(CatalogUrl(product), ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                if (!IsMicrosoftUrl(resp.RequestMessage?.RequestUri?.ToString() ?? ""))
                    throw new ReinstallException("Link danh mục không trỏ về máy chủ Microsoft.");
                using (var o = File.Create(cab)) await resp.Content.CopyToAsync(o).ConfigureAwait(false);
            }
            var r = run.Run("expand.exe", $"\"{cab}\" -F:products.xml \"{xml}\"");
            if (!File.Exists(xml)) throw new ReinstallException("Không giải nén được danh mục bộ cài: " + ProcessRunner.FirstLines(r.Output, 3));
            return Parse(File.ReadAllText(xml), product);
        }

        /// <summary>Chọn file bán lẻ theo ngôn ngữ và kiến trúc (x64 / ARM64).</summary>
        public static EsdFile? Pick(IEnumerable<EsdFile> files, string languageCode, string architecture) =>
            files.Where(f => f.Consumer && f.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase))
                 .FirstOrDefault(f => f.LanguageCode.Equals(languageCode, StringComparison.OrdinalIgnoreCase));

        /// <summary>Kiến trúc của máy theo tên trong danh mục.</summary>
        public static string MachineArchitecture() =>
            string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "ARM64", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432"), "ARM64", StringComparison.OrdinalIgnoreCase)
                ? "ARM64" : "x64";
    }

    /// <summary>Tải file .esd: tải tiếp khi rớt mạng (.part), kiểm SHA1 theo danh mục trước khi dùng.</summary>
    public static class EsdDownloader
    {
        internal static HttpClient Client()
        {
            // .NET Framework 4.8 trên Windows 7 mặc định chưa bật TLS 1.2
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ITHelpdeskTool/1.0");
            return http;
        }

        /// <param name="progress">(đã tải, tổng) byte.</param>
        public static async Task DownloadAsync(EsdFile f, string dest, Action<long, long>? progress = null, Action<string>? log = null, CancellationToken ct = default)
        {
            if (!EsdCatalog.IsMicrosoftUrl(f.Url)) throw new ReinstallException("Link tải không phải máy chủ Microsoft: " + f.Url);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest)) ?? ".");
            if (File.Exists(dest) && new FileInfo(dest).Length == f.Size)
            {
                log?.Invoke("File đã tải sẵn — kiểm tra lại mã SHA1…");
                if (Sha1(dest, progress, ct) == f.Sha1) return;
                File.Delete(dest);
            }
            var part = dest + ".part";
            // Máy chủ dl.delivery.mp.microsoft.com chỉ phục vụ http — toàn vẹn file đảm bảo bằng SHA1 trong danh mục (tải qua https từ download.microsoft.com)
            var urls = new List<string> { f.Url };

            using (var http = Client())
            {
                Exception? last = null;
                for (var attempt = 0; attempt < 8; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var url = urls[attempt % urls.Count];
                    try
                    {
                        await Once(http, url, f.Size, part, progress, ct).ConfigureAwait(false);
                        last = null;
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        last = ex;
                        log?.Invoke($"Lỗi tải ({ex.Message}) — thử lại…");
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 3 * (attempt + 1))), ct).ConfigureAwait(false);
                    }
                }
                if (last != null) throw new ReinstallException("Không tải được bộ cài: " + last.Message + " (bấm tải lại để tải tiếp phần còn thiếu).");
            }

            log?.Invoke("Đang kiểm tra mã SHA1…");
            var sha = Sha1(part, progress, ct);
            if (sha != f.Sha1)
            {
                File.Delete(part);
                throw new ReinstallException($"File tải về sai mã SHA1 (của Microsoft: {f.Sha1}, tải về: {sha}) — đã xoá, hãy tải lại.");
            }
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }

        private static async Task Once(HttpClient http, string url, long total, string part, Action<long, long>? progress, CancellationToken ct)
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have > total) { File.Delete(part); have = 0; }
            if (have == total && total > 0) return;
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    resp.EnsureSuccessStatusCode();
                    // Máy chủ không hỗ trợ tải tiếp → tải lại từ đầu
                    if (have > 0 && resp.StatusCode != HttpStatusCode.PartialContent) have = 0;
                    using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
                    {
                        var buf = new byte[1 << 20];
                        var lastReport = DateTime.MinValue;
                        int n;
                        while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await dst.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                            have += n;
                            if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 500) { lastReport = DateTime.UtcNow; progress?.Invoke(have, total); }
                        }
                    }
                }
            }
            progress?.Invoke(have, total);
            if (have != total) throw new IOException($"Mới tải được {have:N0}/{total:N0} byte");
        }

        public static string Sha1(string path, Action<long, long>? progress = null, CancellationToken ct = default)
        {
            using (var sha = SHA1.Create())
            using (var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buf = new byte[1 << 20];
                long done = 0, total = s.Length;
                var lastReport = DateTime.MinValue;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    sha.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 500) { lastReport = DateTime.UtcNow; progress?.Invoke(done, total); }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }
    }

    /// <summary>
    /// Dựng bộ cài từ file .esd của Media Creation Tool (giống cách Microsoft làm):
    /// ảnh "Windows Setup Media" → thư mục bộ cài, ảnh "Windows PE" → sources\boot.wim, file .esd → sources\install.esd.
    /// </summary>
    public static class MediaBuilder
    {
        public const string DoneTag = "ith-media.done";

        /// <summary>Ảnh trong .esd không phải bản Windows để cài (thư mục bộ cài, WinPE, trình cài đặt).</summary>
        public static bool IsSetupImage(ImageEdition e) =>
            e.Name.StartsWith("Windows Setup Media", StringComparison.OrdinalIgnoreCase) ||
            e.Name.StartsWith("Microsoft Windows PE", StringComparison.OrdinalIgnoreCase) ||
            e.Name.StartsWith("Microsoft Windows Setup", StringComparison.OrdinalIgnoreCase);

        /// <summary>Đường dẫn install.esd nếu thư mục đã dựng xong.</summary>
        public static string? Existing(string mediaDir)
        {
            var esd = Path.Combine(mediaDir, "sources", "install.esd");
            return File.Exists(Path.Combine(mediaDir, DoneTag)) && File.Exists(esd) ? esd : null;
        }

        /// <returns>Đường dẫn sources\install.esd (chọn làm file cài).</returns>
        public static string Build(IRunner run, string esd, string mediaDir, Action<string>? progress = null)
        {
            var log = progress ?? (_ => { });
            var images = ImageInfo.Read(run, esd);
            var media = images.FirstOrDefault(i => i.Name.StartsWith("Windows Setup Media", StringComparison.OrdinalIgnoreCase))
                        ?? throw new ReinstallException("File .esd không có ảnh “Windows Setup Media” — không phải file của Media Creation Tool.");
            var pe = images.FirstOrDefault(i => i.Name.StartsWith("Microsoft Windows PE", StringComparison.OrdinalIgnoreCase))
                     ?? throw new ReinstallException("File .esd không có ảnh Windows PE.");
            if (!images.Any(i => !IsSetupImage(i))) throw new ReinstallException("File .esd không có bản Windows nào.");

            Directory.CreateDirectory(mediaDir);
            var tag = Path.Combine(mediaDir, DoneTag);
            if (File.Exists(tag)) File.Delete(tag);

            log("Đang bung thư mục bộ cài…");
            var r = Wim.Apply(run, esd, media.Index, mediaDir);
            if (!r.Ok) throw new ReinstallException("Không bung được thư mục bộ cài: " + ProcessRunner.FirstLines(r.Output, 3));

            var sources = Path.Combine(mediaDir, "sources");
            Directory.CreateDirectory(sources);
            var bootWim = Path.Combine(sources, "boot.wim");
            if (File.Exists(bootWim)) File.Delete(bootWim);
            log("Đang tạo boot.wim (môi trường cài)…");
            r = Wim.Export(run, esd, pe.Index, bootWim, bootable: false);
            if (!r.Ok) throw new ReinstallException("Không tạo được boot.wim: " + ProcessRunner.FirstLines(r.Output, 3));
            if (!File.Exists(Path.Combine(mediaDir, "boot", "boot.sdi")))
                throw new ReinstallException("Thư mục bộ cài thiếu boot\\boot.sdi.");

            var install = Path.Combine(sources, "install.esd");
            if (!string.Equals(Path.GetFullPath(esd), Path.GetFullPath(install), StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(install)) File.Delete(install);
                File.Move(esd, install);
            }
            File.WriteAllText(tag, DateTime.Now.ToString("s", CultureInfo.InvariantCulture));
            log("Đã dựng xong bộ cài: " + mediaDir);
            return install;
        }
    }
}
