using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Framework.Documents;
using Framework.Expressions;
using Framework.Metadata;
using Framework.Printing;
using Framework.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Framework.Web.Printing;

// ---------- Model cetak (hasil olahan; komponen hanya menampilkan) ----------

public sealed record Letterhead(string CompanyName, string? Address = null, string? Contact = null, string? TaxId = null, string? LogoSvg = null);

public sealed record PrintField(string Label, string Value, bool Wide = false);
public sealed record PrintSection(string? Title, IReadOnlyList<PrintField> Fields);
public sealed record PrintColumn(string Label, bool Numeric);
public sealed record PrintTable(string Title, IReadOnlyList<PrintColumn> Columns, IReadOnlyList<IReadOnlyList<string>> Rows);
public sealed record PrintSignature(string Label, string? Name, DateTimeOffset? At, string? Code);

public sealed record PrintModel
{
    public required Letterhead Letterhead { get; init; }
    public required string Title { get; init; }
    public required string DocName { get; init; }
    public required string DocType { get; init; }
    public required string StateLabel { get; init; }
    public required string StateTone { get; init; }
    public string? Watermark { get; init; }
    public required DateTimeOffset Created { get; init; }
    public required IReadOnlyList<PrintSection> Sections { get; init; }
    public required IReadOnlyList<PrintTable> Tables { get; init; }
    public required IReadOnlyList<PrintField> Totals { get; init; }
    public string? AmountInWords { get; init; }
    public required IReadOnlyList<PrintSignature> Signatures { get; init; }
    public string? FooterNote { get; init; }
    public required string PrintedBy { get; init; }
    public required DateTimeOffset PrintedAt { get; init; }
    /// <summary>Kode verifikasi dokumen: berubah bila isi/versi dokumen berubah.</summary>
    public required string VerificationCode { get; init; }
    public int Version { get; init; }
}

/// <summary>Kop surat per tenant. Ganti implementasinya untuk membaca dari pengaturan/database.</summary>
public interface ILetterheadProvider
{
    Letterhead For(string tenantId);
}

public sealed class ConfigLetterheadProvider(IConfiguration config) : ILetterheadProvider
{
    public Letterhead For(string tenantId)
    {
        var s = config.GetSection($"Print:Letterheads:{tenantId}");
        return new Letterhead(s["CompanyName"] ?? tenantId, s["Address"], s["Contact"], s["TaxId"], s["LogoSvg"]);
    }
}

// ---------- Penyusun model cetak ----------

public sealed class PrintService(DocumentService docs, IMetadataProvider metadata, IPermissionService permissions,
    ILetterheadProvider letterheads, TimeProvider clock)
{
    public async Task<PrintModel> BuildAsync(UserContext user, string docType, string name, CancellationToken ct = default)
    {
        // GetAsync menegakkan permission, scope, tenant, dan redaksi field — field terbatas tidak ikut tercetak.
        var doc = await docs.GetAsync(user, docType, name, ct);
        var meta = metadata.Current.Get(docType);
        var pf = meta.Def.PrintFormat ?? new PrintFormatDef();
        var titles = new Dictionary<(string, string), string?>();

        async Task<string> Display(CompiledField f, object? v)
        {
            if (f.Def.Type == "Link" && v is string link)
            {
                var key = (f.Def.LinkTo!, link);
                if (!titles.TryGetValue(key, out var title))
                {
                    try
                    {
                        var target = await docs.GetAsync(user, f.Def.LinkTo!, link, ct);
                        var tm = metadata.Current.Get(f.Def.LinkTo!);
                        title = tm.Def.ListView.TitleField is { } tf ? target.Data.GetValueOrDefault(tf) as string : null;
                    }
                    catch (Exception ex) when (ex is DocumentNotFoundException or PermissionDeniedException) { title = null; }
                    titles[key] = title;
                }
                return title is null ? link : $"{title} ({link})";
            }
            return Fmt.Value(f, v);
        }

        bool Readable(CompiledField f) => permissions.CanReadField(user, meta, f);

        // Blok isian
        var sectionDefs = pf.Sections ?? meta.Layout.SelectMany(t => t.Sections)
            .Select(s => new PrintSectionDef
            {
                Title = s.Title,
                Fields = s.Columns.SelectMany(c => c.Fields).Where(f => f.Def.Type != "Table").Select(f => f.Name).ToList()
            }).Where(s => s.Fields.Count > 0).ToList();

        var sections = new List<PrintSection>();
        foreach (var sd in sectionDefs)
        {
            var fields = new List<PrintField>();
            foreach (var fname in sd.Fields)
            {
                if (meta.ByName.TryGetValue(fname, out var f))
                {
                    if (!Readable(f) || pf.TotalFields.Contains(fname)) continue;
                    var v = doc.Data.GetValueOrDefault(fname);
                    fields.Add(new PrintField(f.Label, await Display(f, v), f.Def.Type == "Text"));
                }
                else fields.Add(new PrintField(fname, Fmt.Value(null, doc.Get(fname))));
            }
            if (fields.Count > 0) sections.Add(new PrintSection(sd.Title, fields));
        }

        // Child table
        var tables = new List<PrintTable>();
        foreach (var tf in meta.TableFields.Where(Readable))
        {
            var child = metadata.Current.Get(tf.Def.ChildDocType!);
            var colNames = pf.TableColumns?.GetValueOrDefault(tf.Name) ?? child.Fields.Select(c => c.Name).ToList();
            var cols = colNames.Select(n => child.ByName[n]).ToList();
            var rows = new List<IReadOnlyList<string>>();
            foreach (var r in doc.Data.GetValueOrDefault(tf.Name) as List<Dictionary<string, object?>> ?? [])
            {
                var cells = new List<string>();
                foreach (var c in cols) cells.Add(await Display(c, r.GetValueOrDefault(c.Name)));
                rows.Add(cells);
            }
            tables.Add(new PrintTable(tf.Label, cols.Select(c => new PrintColumn(c.Label, c.Type.Editor == "number")).ToList(), rows));
        }

        var totals = pf.TotalFields.Where(t => meta.ByName.TryGetValue(t, out var f) && Readable(f))
            .Select(t => new PrintField(meta.ByName[t].Label, Fmt.Value(meta.ByName[t], doc.Data.GetValueOrDefault(t)))).ToList();

        string? words = null;
        if (pf.AmountInWordsField is { } aw && meta.ByName.TryGetValue(aw, out var awf) && Readable(awf) && Values.ToDecimal(doc.Data.GetValueOrDefault(aw)) is { } amount)
            words = Terbilang.Rupiah(amount);

        // Tanda tangan elektronik dari riwayat workflow
        var audit = await docs.GetAuditAsync(user, docType, name, ct);
        var tone = Fmt.Tone(meta, doc);
        var stillPending = doc.DocStatus == DocStatus.Draft || tone == "pending";
        var signatures = new List<PrintSignature>();
        foreach (var sg in pf.Signatures)
        {
            var entry = audit.Where(a => a.ToState is not null && sg.ToStates.Contains(a.ToState) && (sg.FromState is null || a.FromState == sg.FromState))
                .OrderByDescending(a => a.Id).FirstOrDefault();
            if (entry is null && sg.Optional && !stillPending) continue;
            signatures.Add(entry is null
                ? new PrintSignature(sg.Label, null, null, null)
                : new PrintSignature(sg.Label, entry.Actor, entry.At, Code($"{doc.TenantId}|{doc.Name}|{entry.Id}|{entry.Actor}|{entry.At:O}", 8)));
        }

        var stateLabel = Fmt.StateLabel(doc.WorkflowState, doc.DocStatus);
        string? watermark = !pf.Watermark ? null
            : doc.DocStatus == DocStatus.Draft ? "DRAFT"
            : doc.DocStatus == DocStatus.Cancelled ? stateLabel.ToUpperInvariant()
            : tone == "pending" ? "BELUM DISETUJUI" : null;

        return new PrintModel
        {
            Letterhead = letterheads.For(user.TenantId),
            Title = pf.Title ?? meta.Title,
            DocName = doc.Name,
            DocType = docType,
            StateLabel = stateLabel,
            StateTone = tone,
            Watermark = watermark,
            Created = doc.Created,
            Sections = sections,
            Tables = tables,
            Totals = totals,
            AmountInWords = words,
            Signatures = signatures,
            FooterNote = pf.FooterNote,
            PrintedBy = user.FullName,
            PrintedAt = clock.GetUtcNow(),
            VerificationCode = Code($"{doc.TenantId}|{doc.DocType}|{doc.Name}|{doc.Version}|{doc.Modified:O}", 12),
            Version = doc.Version,
        };
    }

    private static string Code(string input, int length)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..length];
        return string.Join('-', Enumerable.Range(0, length / 4).Select(i => hex.Substring(i * 4, 4)));
    }
}

// ---------- HTML → PDF ----------

public sealed class PdfUnavailableException(string message) : Exception(message);

public interface IPdfRenderer
{
    Task<byte[]> RenderAsync(string html, CancellationToken ct = default);
}

/// <summary>
/// Membuat PDF memakai Chrome/Edge headless yang terpasang di server (tanpa paket NuGet tambahan).
/// Path browser: konfigurasi "Print:BrowserPath", atau dicari otomatis di lokasi umum Windows/Linux/macOS.
/// Alternatif: ganti implementasi IPdfRenderer dengan Microsoft.Playwright atau layanan Gotenberg.
/// </summary>
public sealed class ChromiumPdfRenderer(IConfiguration config, ILogger<ChromiumPdfRenderer> log) : IPdfRenderer
{
    private static readonly SemaphoreSlim Gate = new(2);

    private static readonly string[] Candidates =
    [
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser",
        "/usr/bin/microsoft-edge", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    ];

    private string? BrowserPath => config["Print:BrowserPath"] is { Length: > 0 } p && File.Exists(p) ? p : Candidates.FirstOrDefault(File.Exists);

    public async Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
    {
        var browser = BrowserPath ?? throw new PdfUnavailableException(
            "Chrome/Edge tidak ditemukan di server. Atur 'Print:BrowserPath' di appsettings, atau gunakan tombol Cetak → Simpan sebagai PDF.");

        await Gate.WaitAsync(ct);
        var work = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "docframe-pdf-" + Guid.NewGuid().ToString("N")));
        try
        {
            var htmlPath = Path.Combine(work.FullName, "doc.html");
            var pdfPath = Path.Combine(work.FullName, "doc.pdf");
            await File.WriteAllTextAsync(htmlPath, html, Encoding.UTF8, ct);

            var psi = new ProcessStartInfo(browser) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[]
            {
                "--headless=new", "--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions",
                "--disable-background-networking", "--hide-scrollbars", $"--user-data-dir={Path.Combine(work.FullName, "profile")}",
                "--no-pdf-header-footer", "--run-all-compositor-stages-before-draw", "--virtual-time-budget=5000",
                $"--print-to-pdf={pdfPath}", new Uri(htmlPath).AbsoluteUri,
            }) psi.ArgumentList.Add(a);
            if (config.GetValue("Print:NoSandbox", OperatingSystem.IsLinux())) psi.ArgumentList.Insert(0, "--no-sandbox");

            using var proc = Process.Start(psi) ?? throw new PdfUnavailableException("Gagal menjalankan browser untuk PDF");
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.GetValue("Print:TimeoutSeconds", 30)));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch { } throw new PdfUnavailableException("Pembuatan PDF melebihi batas waktu"); }

            if (!File.Exists(pdfPath))
            {
                log.LogError("Chrome gagal membuat PDF (exit {Code}): {Err}", proc.ExitCode, await stderr);
                throw new PdfUnavailableException("Browser gagal membuat PDF");
            }
            return await File.ReadAllBytesAsync(pdfPath, ct);
        }
        finally
        {
            Gate.Release();
            try { work.Delete(true); } catch { /* profil Chrome kadang masih terkunci sesaat */ }
        }
    }
}

/// <summary>Merender PrintModel menjadi dokumen HTML lengkap (dipakai untuk PDF).</summary>
public sealed class PrintHtmlRenderer(IServiceScopeFactory scopes, ILoggerFactory loggers)
{
    public async Task<string> RenderAsync(PrintModel model)
    {
        // HtmlRenderer membutuhkan service ber-scope (mis. ComponentsActivitySource di .NET 10), jadi dibuat
        // di dalam scope DI sendiri, bukan dari root provider.
        await using var scope = scopes.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, loggers);
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<PrintHtmlDocument>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PrintHtmlDocument.Model)] = model }));
            return "<!DOCTYPE html>" + output.ToHtmlString();
        });
    }
}
