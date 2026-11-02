using System.Text.Json;
using Framework.Documents;
using Framework.Jobs;
using Framework.Metadata;
using Framework.Persistence;
using Framework.Security;
using Framework.Web.Printing;
using Framework.Web.Identity;
using Microsoft.AspNetCore.Identity;
using Framework.Files;
using Framework.Reports;
using Framework.Api;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using Framework.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Web;

public static class DocApi
{
    public static IEndpointRouteBuilder MapDocFrameworkApi(this IEndpointRouteBuilder app)
    {
        // ---------- File (gambar) ----------
        // Cookie browser diterima (dipakai <img>). Tertaut ke dokumen → hanya yang boleh membaca dokumen itu dan
        // dokumen masih memakai file tersebut; belum tertaut → hanya pengunggah. Selain itu 404 (tidak membocorkan keberadaan).
        app.MapGet("/files/{id}", async (HttpContext http, string id) =>
        {
            var user = ClaimsUserMapper.ToUserContext(http.User);
            if (user is null) return Results.Unauthorized();
            var sp = http.RequestServices;
            var file = await sp.GetRequiredService<IFileStore>().GetAsync(user.TenantId, id, http.RequestAborted);
            if (file is not { } f) return Results.NotFound();
            bool allowed;
            if (f.Record.DocName is null || f.Record.DocType == "UserAccount")
            {
                allowed = f.Record.DocType == "UserAccount" || f.Record.UploadedBy == user.Id;
            }
            else
            {
                try
                {
                    var doc = await Docs(sp).GetAsync(user, f.Record.DocType!, f.Record.DocName, http.RequestAborted);
                    allowed = f.Record.Field is { } field && doc.Data.GetValueOrDefault(field) as string == id;
                }
                catch (Exception ex) when (ex is DocumentNotFoundException or PermissionDeniedException or KeyNotFoundException) { allowed = false; }
            }
            if (!allowed) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; img-src 'self'; sandbox";
            http.Response.Headers.CacheControl = "private, max-age=300";
            return Results.File(f.Content, f.Record.ContentType);
        });

        // ---------- Laporan file (Excel/Word) ----------
        // Unduhan dari halaman laporan: token sekali pakai yang terikat pengguna (cookie browser diterima, GET).
        app.MapGet("/report-downloads/{token}", (HttpContext http, string token) =>
        {
            var user = ClaimsUserMapper.ToUserContext(http.User);
            if (user is null) return Results.Unauthorized();
            var output = http.RequestServices.GetRequiredService<ReportDownloads>().Take(token, user.TenantId, user.Id);
            if (output is null) return Results.NotFound();
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(output.Content, output.ContentType, output.FileName);
        });

        var api = app.MapGroup("/api").RequireRateLimiting(ServiceCollectionExtensions.ApiRateLimit);

        // ---------- API dari query tersimpan: GET /api/query/{slug}?Param=nilai ----------
        api.MapGet("/query", (Delegate)((HttpContext http) => Run(http, AnyApiKeyScope, async (user, sp) =>
        {
            var granted = http.User.FindAll(Framework.Web.Identity.ApiKeyDefaults.ScopeClaim).Select(c => c.Value).ToList();
            var viaKey = http.User.FindFirst(Framework.Web.Identity.ApiKeyDefaults.KeyIdClaim) is not null;
            var list = (await sp.GetRequiredService<QueryApiService>().ListAsync(user, http.RequestAborted))
                .Where(d => d.Config.Enabled && QueryApiService.CanCall(user, d) && (!viaKey || ApiScopes.Allows(granted, $"query:{d.Slug}")))
                .Select(d => new
                {
                    slug = d.Slug, name = d.Name, description = d.Description, output = d.Config.Output, endpoint = $"/api/query/{d.Slug}",
                    parameters = d.Config.Parameters.Where(p => !QueryApiService.IsUserBound(p)).Select(p => new { name = p.Name, label = p.Label, type = p.Type, @default = p.DefaultValue }),
                });
            return Results.Ok(list);
        })));

        api.MapGet("/query/{slug}", (HttpContext http, string slug) => Run(http, $"query:{slug}", async (user, sp) =>
        {
            var args = http.Request.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var r = await sp.GetRequiredService<QueryApiService>().ExecuteAsync(user, slug, args, http.RequestAborted);
            http.Response.Headers["X-Cache"] = r.FromCache ? "HIT" : "MISS";
            var cols = r.Data.Columns;
            Dictionary<string, object?> Row(object?[] row) => cols.Select((c, i) => (c.Name, row[i])).ToDictionary(x => x.Name, x => x.Item2);
            return r.Def.Config.Output switch
            {
                QueryApiOutputs.Object => Results.Ok(new { data = r.Data.Rows.Count > 0 ? Row(r.Data.Rows[0]) : null, columns = cols }),
                QueryApiOutputs.Csv => Results.File(Csv(cols, r.Data.Rows), "text/csv; charset=utf-8", $"{slug}.csv"),
                _ => Results.Ok(new { data = r.Data.Rows.Select(Row), columns = cols, count = r.Data.Rows.Count, truncated = r.Data.Truncated }),
            };
        }));

        // ---------- Method kustom (padanan /api/method di Frappe) ----------
        api.MapGet("/method", (Delegate)((HttpContext http) => Run(http, AnyApiKeyScope, (user, sp) =>
        {
            var granted = http.User.FindAll(Framework.Web.Identity.ApiKeyDefaults.ScopeClaim).Select(c => c.Value).ToList();
            var viaKey = http.User.FindFirst(Framework.Web.Identity.ApiKeyDefaults.KeyIdClaim) is not null;
            var list = sp.GetRequiredService<ApiMethodRegistry>().Visible(user)
                .Where(m => !viaKey || ApiScopes.Allows(granted, $"method:{m.Name}"))
                .Select(m => new { name = m.Name, description = m.Description, roles = m.Roles, parameters = m.Parameters, endpoint = $"/api/method/{m.Name}" });
            return Task.FromResult(Results.Ok(list));
        })));

        api.MapPost("/method/{name}", (HttpContext http, string name) => Run(http, $"method:{name}", async (user, sp) =>
        {
            JsonElement? body = null;
            if (http.Request.ContentLength is > 0 || http.Request.Headers.TransferEncoding.Count > 0)
            {
                using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                body = doc.RootElement.Clone();
            }
            var result = await sp.GetRequiredService<ApiMethodRegistry>().InvokeAsync(user, name, body, sp, http.RequestAborted);
            return Results.Ok(new { data = result });
        }));

        // Klien API (bearer token): POST /api/report-templates/{id}/generate  { "parameters": { "Periode": "2026-10-01" } }
        api.MapPost("/report-templates/{id}/generate", (HttpContext http, string id, GenerateReportBody? body) => Run(http, $"report:{id}", async (user, sp) =>
        {
            try
            {
                var output = await sp.GetRequiredService<ReportTemplateService>().GenerateAsync(user, id, body?.Parameters ?? new Dictionary<string, string?>(), http.RequestAborted);
                return Results.File(output.Content, output.ContentType, output.FileName);
            }
            catch (Exception ex) when (ex is TemplateException or Framework.Dashboards.QueryRejectedException or Framework.Dashboards.ReportingUnavailableException)
            {
                return Problem(400, "Laporan tidak bisa dibuat", ex.Message);
            }
        }));

        // ---------- Autentikasi API ----------
        api.MapPost("/auth/login", async (LoginBody body, SignInManager<UserAccount> signIn) =>
        {
            // Respons token (access_token, expires_in, refresh_token) ditulis langsung oleh handler bearer token.
            signIn.AuthenticationScheme = IdentityConstants.BearerScheme;
            var result = await signIn.PasswordSignInAsync(body.UserName ?? "", body.Password ?? "", isPersistent: false, lockoutOnFailure: true);
            if (result.Succeeded) return Results.Empty;
            return result.IsLockedOut ? Problem(423, "Akun terkunci", "Terlalu banyak percobaan gagal. Coba lagi nanti.")
                : Problem(401, "Login gagal", "Username atau password salah, atau akun nonaktif.");
        }).AllowAnonymous();

        api.MapGet("/auth/me", (HttpContext http) => ClaimsUserMapper.ToUserContext(http.User) is { } u
            ? Results.Ok(u) : Problem(401, "Belum terautentikasi", "Tidak ada sesi atau token yang valid."));

        // ---------- Metadata ----------
        api.MapGet("/meta/{doctype}", (HttpContext http, string doctype) => Run(http, $"doc:{doctype}:read", (user, sp) =>
        {
            var meta = sp.GetRequiredService<IMetadataProvider>().Current.Get(doctype);
            var perms = sp.GetRequiredService<IPermissionService>();
            perms.Demand(user, meta, DocActions.Read);
            return Task.FromResult(Results.Ok(new
            {
                meta.Name, meta.Version, meta.Title, meta.Def.IsChild, meta.Def.Workflow,
                fields = meta.Fields.Where(f => perms.CanReadField(user, meta, f)).Select(f => f.Def),
                meta.Def.ListView, meta.Def.Rules,
                can = DocActions.All.Where(a => perms.Can(user, meta, a)),
            }));
        }));

        api.MapGet("/meta/{doctype}/versions", (HttpContext http, string doctype) => Run(http, (user, sp) =>
        {
            RequireRole(user, UserContext.SystemManager);
            return Task.FromResult(Results.Ok(sp.GetRequiredService<IMetadataProvider>().History(doctype)
                .Select(v => new { v.Version, status = v.Status.ToString(), v.PublishedBy, v.PublishedAt, v.Changes })));
        }));

        api.MapGet("/meta/{doctype}/versions/{version:int}", (HttpContext http, string doctype, int version) => Run(http, (user, sp) =>
        {
            RequireRole(user, UserContext.SystemManager);
            var v = sp.GetRequiredService<IMetadataProvider>().History(doctype).FirstOrDefault(x => x.Version == version)
                ?? throw new DocumentNotFoundException("Versi metadata", $"{doctype} v{version}");
            return Task.FromResult(Results.Ok(v.Def));
        }));

        api.MapPost("/admin/metadata/preview", (HttpContext http, JsonElement body) => Run(http, (user, sp) =>
        {
            RequireRole(user, UserContext.SystemManager);
            var def = MetadataJson.ParseDocType(body.GetRawText());
            return Task.FromResult(Results.Ok(sp.GetRequiredService<MetadataRegistry>().Preview(def)));
        }));

        api.MapPost("/admin/metadata/publish", (HttpContext http, JsonElement body, bool acknowledgeRisk) => Run(http, (user, sp) =>
        {
            RequireRole(user, UserContext.SystemManager);
            var def = MetadataJson.ParseDocType(body.GetRawText());
            var v = sp.GetRequiredService<MetadataRegistry>().Publish(def, user.Id, acknowledgeRisk);
            return Task.FromResult(Results.Ok(new { v.DocType, v.Version, v.Changes }));
        }));

        // ---------- Data ----------
        api.MapGet("/data/{doctype}", (HttpContext http, string doctype, string? search, string? sort, string? dir, int? page, int? pageSize, string? filters) =>
            Run(http, $"doc:{doctype}:list", async (user, sp) =>
            {
                RequireApi(sp, doctype, "list");
                var parsed = ParseFilters(filters);
                var res = await Docs(sp).ListAsync(user, doctype, new ListRequest(parsed, search, sort, dir, page ?? 1, pageSize ?? 0), http.RequestAborted);
                return Results.Ok(res);
            }));

        api.MapGet("/data/{doctype}/{name}", (HttpContext http, string doctype, string name) => Run(http, $"doc:{doctype}:read", async (user, sp) =>
        {
            RequireApi(sp, doctype, "read");
            var doc = await Docs(sp).GetAsync(user, doctype, name, http.RequestAborted);
            http.Response.Headers.ETag = $"\"{doc.Version}\"";
            return Results.Ok(ToDto(doc));
        }));

        api.MapPost("/data/{doctype}", (HttpContext http, string doctype, Dictionary<string, JsonElement> body) => Run(http, $"doc:{doctype}:create", async (user, sp) =>
        {
            RequireApi(sp, doctype, "create");
            var doc = await Docs(sp).InsertAsync(user, doctype, ToPayload(body), http.RequestAborted);
            return Results.Created($"/api/data/{doctype}/{doc.Name}", ToDto(doc));
        }));

        api.MapMethods("/data/{doctype}/{name}", ["PATCH"], (HttpContext http, string doctype, string name, Dictionary<string, JsonElement> body) => Run(http, $"doc:{doctype}:update", async (user, sp) =>
        {
            RequireApi(sp, doctype, "update");
            var doc = await Docs(sp).UpdateAsync(user, doctype, name, IfMatch(http), ToPayload(body), http.RequestAborted);
            http.Response.Headers.ETag = $"\"{doc.Version}\"";
            return Results.Ok(ToDto(doc));
        }));

        api.MapDelete("/data/{doctype}/{name}", (HttpContext http, string doctype, string name) => Run(http, $"doc:{doctype}:delete", async (user, sp) =>
        {
            RequireApi(sp, doctype, "delete");
            await Docs(sp).DeleteAsync(user, doctype, name, IfMatch(http), http.RequestAborted);
            return Results.NoContent();
        }));

        api.MapPost("/data/{doctype}/{name}/actions/{action}", (HttpContext http, string doctype, string name, string action, ActionBody? body) => Run(http, $"doc:{doctype}:action", async (user, sp) =>
        {
            RequireApi(sp, doctype, "action");
            var doc = await Docs(sp).RunActionAsync(user, doctype, name, action, IfMatch(http), body?.Comment, http.RequestAborted);
            http.Response.Headers.ETag = $"\"{doc.Version}\"";
            return Results.Ok(ToDto(doc));
        }));

        api.MapGet("/data/{doctype}/{name}/audit", (HttpContext http, string doctype, string name) => Run(http, $"doc:{doctype}:read", async (user, sp) =>
        {
            RequireApi(sp, doctype, "read");
            return Results.Ok(await Docs(sp).GetAuditAsync(user, doctype, name, http.RequestAborted));
        }));

        api.MapGet("/links/{target}", (HttpContext http, string target, string? q) => Run(http, $"doc:{target}:list", async (user, sp) =>
            Results.Ok(await Docs(sp).SearchLinkAsync(user, target, q, http.RequestAborted))));

        // ---------- Report ----------
        api.MapGet("/reports/{name}", (HttpContext http, string name) => Run(http, async (user, sp) =>
            Results.Ok(await sp.GetRequiredService<ReportService>().RunAsync(user, name, QueryDict(http), http.RequestAborted))));

        api.MapGet("/reports/{name}/export.csv", (HttpContext http, string name) => Run(http, async (user, sp) =>
        {
            var csv = await sp.GetRequiredService<ReportService>().ExportCsvAsync(user, name, QueryDict(http), http.RequestAborted);
            return Results.File(System.Text.Encoding.UTF8.GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv", $"{name}.csv");
        }));

        // ---------- Cetak ----------
        api.MapGet("/print/{doctype}/{name}/pdf", (HttpContext http, string doctype, string name, bool? inline) => Run(http, $"doc:{doctype}:read", async (user, sp) =>
        {
            var model = await sp.GetRequiredService<PrintService>().BuildAsync(user, doctype, name, http.RequestAborted);
            var html = await sp.GetRequiredService<PrintHtmlRenderer>().RenderAsync(model);
            var pdf = await sp.GetRequiredService<IPdfRenderer>().RenderAsync(html, http.RequestAborted);
            await Docs(sp).LogPrintAsync(user, doctype, name, "PDF", http.RequestAborted);
            return inline == true ? Results.File(pdf, "application/pdf") : Results.File(pdf, "application/pdf", $"{name}.pdf");
        }));

        return app;
    }

    public sealed record ActionBody(string? Comment);
    public sealed record LoginBody(string? UserName, string? Password);
    public sealed record GenerateReportBody(Dictionary<string, string?>? Parameters);

    private static DocumentService Docs(IServiceProvider sp) => sp.GetRequiredService<DocumentService>();

    private static void RequireRole(UserContext user, string role)
    {
        if (!user.Roles.Contains(role)) throw new PermissionDeniedException($"Butuh role '{role}'");
    }

    /// <summary>API generik opt-in per DocType (RFC §13).</summary>
    private static void RequireApi(IServiceProvider sp, string doctype, string op)
    {
        if (!sp.GetRequiredService<IMetadataProvider>().Current.TryGet(doctype, out var meta) || meta.Def.IsChild)
            throw new DocumentNotFoundException("DocType", doctype);
        if (!meta.Def.Api.Enabled || !meta.Def.Api.Operations.Contains(op))
            throw new DocumentNotFoundException($"Operasi API '{op}' untuk", doctype);
    }

    private static int IfMatch(HttpContext http)
    {
        var raw = http.Request.Headers.IfMatch.FirstOrDefault()?.Trim('"', ' ');
        return int.TryParse(raw, out var v) ? v : throw new InvalidPayloadException("Header If-Match berisi versi dokumen wajib dikirim");
    }

    private static Dictionary<string, object?> ToPayload(Dictionary<string, JsonElement> body) =>
        body.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

    private static Dictionary<string, string?> QueryDict(HttpContext http) =>
        http.Request.Query.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString());

    private static List<FilterClause> ParseFilters(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateArray().Select(e =>
            {
                var a = e.EnumerateArray().ToArray();
                if (a.Length != 3) throw new InvalidPayloadException("Filter harus berupa [field, operator, nilai]");
                object? value = a[2].ValueKind == JsonValueKind.Array
                    ? a[2].EnumerateArray().Select(Scalar).ToList()
                    : Scalar(a[2]);
                return new FilterClause(a[0].GetString()!, a[1].GetString()!, value);
            }).ToList();
        }
        catch (JsonException) { throw new InvalidPayloadException("Parameter filters bukan JSON yang valid"); }
    }

    private static object? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => throw new InvalidPayloadException("Nilai filter harus skalar atau daftar skalar")
    };

    public static Dictionary<string, object?> ToDto(Document d)
    {
        var dto = new Dictionary<string, object?>
        {
            ["name"] = d.Name, ["doctype"] = d.DocType, ["docstatus"] = d.DocStatus, ["workflow_state"] = d.WorkflowState,
            ["owner"] = d.Owner, ["created"] = d.Created, ["modified"] = d.Modified, ["modified_by"] = d.ModifiedBy,
            ["version"] = d.Version, ["schema_version"] = d.SchemaVersion, ["workflow_version"] = d.WorkflowVersion,
            ["amended_from"] = d.AmendedFrom,
        };
        foreach (var (k, v) in d.Data) dto[k] = v;
        return dto;
    }

    /// <summary>Pemetaan exception → response yang konsisten (application/problem+json).</summary>
    /// <summary>Endpoint tanpa deklarasi cakupan: kunci API ditolak (gagal tertutup).</summary>
    private static Task<IResult> Run(HttpContext http, Func<UserContext, IServiceProvider, Task<IResult>> action) => Run(http, null, action);

    /// <summary>Cakupan khusus: kunci API boleh masuk tanpa dicek di sini (endpoint menyaring sendiri, mis. daftar method).</summary>
    private const string AnyApiKeyScope = "*any";

    private static async Task<IResult> Run(HttpContext http, string? scope, Func<UserContext, IServiceProvider, Task<IResult>> action)
    {
        // Kunci API: hanya endpoint yang mendeklarasikan cakupan, dan cakupan itu harus diberikan pada kunci.
        if (http.User.FindFirst(Framework.Web.Identity.ApiKeyDefaults.KeyIdClaim) is not null && scope != AnyApiKeyScope)
        {
            if (scope is null) return Problem(403, "Kunci API tidak berlaku", "Endpoint ini tidak bisa dipakai dengan kunci API.");
            if (!ApiScopes.Allows(http.User.FindAll(Framework.Web.Identity.ApiKeyDefaults.ScopeClaim).Select(c => c.Value), scope))
                return Problem(403, "Di luar cakupan kunci API", $"Kunci ini tidak mencakup '{scope}'.");
        }
        // Identitas dari klaim (cookie atau bearer) yang sudah divalidasi middleware autentikasi.
        var user = ClaimsUserMapper.ToUserContext(http.User);
        if (user is null) return Problem(401, "Belum terautentikasi", "Login lewat POST /api/auth/login lalu kirim header Authorization: Bearer <token>.");
        // Cookie dikirim otomatis oleh browser → permintaan yang mengubah data wajib memakai bearer token (perlindungan CSRF).
        var authz = http.Request.Headers.Authorization.ToString();
        var viaBearer = authz.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authz.StartsWith(Framework.Web.Identity.ApiKeyDefaults.Scheme + " ", StringComparison.OrdinalIgnoreCase);
        if (!viaBearer && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
            return Problem(403, "Butuh bearer token", "API yang mengubah data harus memakai header Authorization: Bearer <token>, bukan cookie browser.");
        try { return await action(user, http.RequestServices); }
        catch (ValidationFailedException ex) { return Problem(422, "Validasi gagal", ex.Message, ex.Errors); }
        catch (PermissionDeniedException ex) { return Problem(403, "Akses ditolak", ex.Message); }
        catch (DocumentNotFoundException ex) { return Problem(404, "Tidak ditemukan", ex.Message); }
        catch (KeyNotFoundException ex) { return Problem(404, "Tidak ditemukan", ex.Message); }
        catch (ConcurrencyException ex) { return Problem(409, "Konflik versi", ex.Message); }
        catch (InvalidStateException ex) { return Problem(409, "Status dokumen tidak mengizinkan", ex.Message); }
        catch (LinkedDocumentException ex) { return Problem(409, "Dokumen masih ditautkan", ex.Message); }
        catch (InvalidPayloadException ex) { return Problem(400, "Payload tidak valid", ex.Message, ex.Fields.Select(f => new FieldError(f, "tidak diizinkan")).ToList()); }
        catch (MetadataCompilationException ex) { return Problem(422, "Metadata tidak valid", "Periksa daftar error", ex.Errors.Select(e => new FieldError(e.Path, e.Message)).ToList()); }
        catch (JsonException ex) { return Problem(400, "JSON tidak valid", ex.Message); }
        catch (PdfUnavailableException ex) { return Problem(501, "PDF tidak tersedia", ex.Message); }
        catch (InvalidOperationException ex) { return Problem(409, "Operasi ditolak", ex.Message); }
        catch (ApiMethodException ex) { return Problem(400, "Permintaan ditolak", ex.Message); }
        catch (Framework.Dashboards.QueryRejectedException ex) { return Problem(400, "Query ditolak", ex.Message); }
        catch (Framework.Dashboards.ReportingUnavailableException ex) { return Problem(503, "Fitur query tidak tersedia", ex.Message); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Jangan bocorkan detail internal (stack, SQL) ke klien API; detail ada di log server.
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Framework.Api").LogError(ex, "API {Method} {Path} gagal", http.Request.Method, http.Request.Path);
            DocFrameTelemetry.HttpErrors.Add(1);
            try
            {
                if (http.RequestServices.GetService<IErrorLogStore>() is { } errorStore)
                {
                    _ = errorStore.LogAsync(new ErrorLogEntry
                    {
                        TenantId = user?.TenantId ?? "acme",
                        Timestamp = DateTimeOffset.UtcNow,
                        Method = http.Request.Method,
                        Path = http.Request.Path.Value ?? "",
                        UserId = user?.Id ?? http.User.Identity?.Name,
                        ErrorType = ex.GetType().FullName ?? ex.GetType().Name,
                        Message = ex.Message,
                        StackTrace = ex.StackTrace,
                        Seen = false
                    });
                }
            }
            catch
            {
                // Ignore failure in error logging
            }
            return Problem(500, "Kesalahan internal", "Terjadi kesalahan di server. Hubungi administrator (rujuk waktu kejadian).");
        }
    }

    /// <summary>CSV UTF-8 (BOM agar Excel membaca huruf dengan benar). Nilai berawalan = + - @ diberi apostrof:
    /// mencegah formula injection saat file dibuka di spreadsheet.</summary>
    private static byte[] Csv(IReadOnlyList<Framework.Dashboards.ResultColumn> cols, IReadOnlyList<object?[]> rows)
    {
        static string Cell(object? v)
        {
            var s = v switch { null => "", decimal d => d.ToString(System.Globalization.CultureInfo.InvariantCulture), bool b => b ? "true" : "false", _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "" };
            if (s.Length > 0 && v is string && "=+-@".Contains(s[0])) s = "'" + s;
            return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", cols.Select(c => Cell(c.Name))));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Cell)));
        return [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(sb.ToString())];
    }

    private static IResult Problem(int status, string title, string detail, IReadOnlyList<FieldError>? errors = null) =>
        Results.Json(new { type = $"https://httpstatuses.io/{status}", title, status, detail, errors = errors ?? [] },
            statusCode: status, contentType: "application/problem+json");
}
