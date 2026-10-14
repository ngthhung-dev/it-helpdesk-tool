using System.IO;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Windows;
using ITHelpdeskTool.Core;

namespace ITHelpdeskTool.Services.Report;

/// <summary>Xuất báo cáo ra HTML (in/PDF được), TXT và JSON.</summary>
public static class ReportExporter
{
    public sealed record Meta(DateTime ScannedAt, TimeSpan Elapsed)
    {
        public string Title => $"Báo cáo cấu hình – {Environment.MachineName}";
        public string Footer => $"Tạo bởi {AppInfo.Name} {AppInfo.Version} · {AppInfo.Author} · {AppInfo.Email} · {AppInfo.Phone}";
    }

    public static string SuggestedFileName(DateTime at) =>
        $"BaoCao_{Environment.MachineName}_{at:yyyyMMdd-HHmm}";

    // ===================== TXT =====================
    public static string ToText(IReadOnlyList<ReportSection> sections, IReadOnlyList<ReportIssue> issues, Meta meta)
    {
        var sb = new StringBuilder();
        sb.AppendLine(meta.Title.ToUpperInvariant());
        sb.AppendLine($"Thời điểm quét: {meta.ScannedAt:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine(new string('=', 72));

        if (issues.Count > 0)
        {
            sb.AppendLine().AppendLine("[ĐIỂM CẦN CHÚ Ý]");
            foreach (var i in issues)
                sb.AppendLine($"  {(i.Tone == Tone.Bad ? "(!!)" : "(!) ")} {i.Section}: {i.What}");
        }

        foreach (var s in sections)
        {
            sb.AppendLine().AppendLine($"[{s.Title.ToUpperInvariant()}]");
            if (s.State == SectionState.Skipped || s.State == SectionState.Error)
            {
                sb.AppendLine($"  {s.Message}");
                continue;
            }

            var w = s.Fields.Count == 0 ? 0 : s.Fields.Max(f => f.Key.Length);
            foreach (var f in s.Fields)
                sb.AppendLine($"  {f.Key.PadRight(w)} : {f.Value}");

            foreach (var t in s.Tables)
            {
                if (t.Rows.Count == 0) continue;
                sb.AppendLine().AppendLine($"  -- {t.Title} --");
                var widths = t.Columns.Select((c, i) =>
                    Math.Min(40, Math.Max(c.Length, t.Rows.Max(r => i < r.Length ? r[i].Text.Length : 0)))).ToArray();
                string Line(IEnumerable<string> cells) =>
                    "  " + string.Join(" | ", cells.Select((c, i) => Cut(c, widths[i]).PadRight(widths[i])));
                sb.AppendLine(Line(t.Columns));
                sb.AppendLine("  " + string.Join("-+-", widths.Select(x => new string('-', x))));
                foreach (var r in t.Rows) sb.AppendLine(Line(r.Select(c => c.Text)));
            }
        }

        sb.AppendLine().AppendLine(new string('=', 72)).AppendLine(meta.Footer);
        return sb.ToString();
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ===================== JSON =====================
    public static string ToJson(IReadOnlyList<ReportSection> sections, IReadOnlyList<ReportIssue> issues, Meta meta)
    {
        var obj = new
        {
            computer = Environment.MachineName,
            scannedAt = meta.ScannedAt,
            generator = $"{AppInfo.Name} {AppInfo.Version}",
            issues = issues.Select(i => new { section = i.Section, what = i.What, level = i.Tone.ToString().ToLowerInvariant() }),
            sections = sections.Select(s => new
            {
                key = s.Key,
                title = s.Title,
                state = s.State.ToString().ToLowerInvariant(),
                message = s.Message.Length > 0 ? s.Message : null,
                fields = s.Fields.GroupBy(f => f.Key).ToDictionary(g => g.Key, g => g.First().Value),
                tables = s.Tables.Select(t => new
                {
                    title = t.Title,
                    rows = t.Rows.Select(r => t.Columns.Select((c, i) => (c, v: i < r.Length ? r[i].Text : ""))
                        .ToDictionary(x => x.c, x => x.v)),
                }),
            }),
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }

    // ===================== HTML =====================
    public static string ToHtml(IReadOnlyList<ReportSection> sections, IReadOnlyList<ReportIssue> issues, Meta meta)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        static string Cls(Tone t) => t switch
        {
            Tone.Good => " class=\"good\"",
            Tone.Warn => " class=\"warn\"",
            Tone.Bad => " class=\"bad\"",
            Tone.Muted => " class=\"muted\"",
            _ => "",
        };

        var sb = new StringBuilder();
        sb.Append($$$"""
            <!doctype html>
            <html lang="vi"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{{E(meta.Title)}}}</title>
            <style>
            :root{--bg:#f6f7fb;--card:#fff;--text:#1d2230;--muted:#6b7385;--line:#e6e8ef;--accent:#5b6cff;--good:#0f9d6b;--warn:#b7791f;--bad:#d64545}
            *{box-sizing:border-box}
            body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
            .wrap{max-width:1080px;margin:0 auto;padding:32px 20px 48px}
            header{display:flex;gap:16px;align-items:center;margin-bottom:24px}
            header img{width:56px;height:56px;border-radius:14px}
            h1{font-size:24px;margin:0}
            .sub{color:var(--muted)}
            .card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin-bottom:16px;break-inside:avoid}
            h2{font-size:16px;margin:0 0 12px}
            h3{font-size:13px;margin:16px 0 8px;color:var(--muted);text-transform:uppercase;letter-spacing:.04em}
            .kv{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:6px 28px}
            .kv div{display:flex;gap:12px;border-bottom:1px dashed var(--line);padding:4px 0}
            .kv b{font-weight:400;color:var(--muted);min-width:160px}
            .kv span{word-break:break-word}
            table{width:100%;border-collapse:collapse;font-size:13px}
            th{text-align:left;color:var(--muted);font-weight:600;border-bottom:1px solid var(--line);padding:6px 8px}
            td{border-bottom:1px solid var(--line);padding:6px 8px;vertical-align:top;word-break:break-word}
            tr:last-child td{border-bottom:0}
            .good{color:var(--good);font-weight:600}.warn{color:var(--warn);font-weight:600}.bad{color:var(--bad);font-weight:600}.muted{color:var(--muted)}
            .issues li{margin:4px 0}
            .ok{color:var(--good)}
            footer{color:var(--muted);font-size:12px;text-align:center;margin-top:28px}
            @media (max-width:720px){.kv{grid-template-columns:1fr}}
            @media print{body{background:#fff}.wrap{padding:0}.card{border-color:#ccc}}
            </style></head><body><div class="wrap">
            """);

        var icon = IconDataUri();
        sb.Append("<header>");
        if (icon is not null) sb.Append($"<img src=\"{icon}\" alt=\"\">");
        sb.Append($"<div><h1>{E(meta.Title)}</h1><div class=\"sub\">Quét lúc {meta.ScannedAt:HH:mm dd/MM/yyyy} · mất {meta.Elapsed.TotalSeconds:0.0} giây</div></div></header>");

        sb.Append("<section class=\"card\"><h2>Điểm cần chú ý</h2>");
        if (issues.Count == 0) sb.Append("<div class=\"ok\">✓ Không phát hiện vấn đề nào.</div>");
        else
        {
            sb.Append("<ul class=\"issues\">");
            foreach (var i in issues)
                sb.Append($"<li><span{Cls(i.Tone)}>{(i.Tone == Tone.Bad ? "Nghiêm trọng" : "Cần xem")}</span> · <b>{E(i.Section)}</b>: {E(i.What)}</li>");
            sb.Append("</ul>");
        }
        sb.Append("</section>");

        foreach (var s in sections)
        {
            sb.Append($"<section class=\"card\"><h2>{E(s.Title)}</h2>");
            if (s.State is SectionState.Skipped or SectionState.Error)
            {
                sb.Append($"<div class=\"muted\">{E(s.Message)}</div></section>");
                continue;
            }
            if (s.Fields.Count > 0)
            {
                sb.Append("<div class=\"kv\">");
                foreach (var f in s.Fields)
                    sb.Append($"<div><b>{E(f.Key)}</b><span{Cls(f.Tone)}>{E(f.Value)}</span></div>");
                sb.Append("</div>");
            }
            foreach (var t in s.Tables)
            {
                if (t.Rows.Count == 0) continue;
                sb.Append($"<h3>{E(t.Title)}</h3><table><thead><tr>");
                foreach (var c in t.Columns) sb.Append($"<th>{E(c)}</th>");
                sb.Append("</tr></thead><tbody>");
                foreach (var r in t.Rows)
                {
                    sb.Append("<tr>");
                    foreach (var c in r) sb.Append($"<td{Cls(c.Tone)}>{E(c.Text)}</td>");
                    sb.Append("</tr>");
                }
                sb.Append("</tbody></table>");
            }
            sb.Append("</section>");
        }

        sb.Append($"<footer>{E(meta.Footer)}<br>{E(AppInfo.Copyright)}</footer></div></body></html>");
        return sb.ToString();
    }

    private static string? IconDataUri()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.png"));
            if (res is null) return null;
            using var ms = new MemoryStream();
            res.Stream.CopyTo(ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    public static void Save(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
}
