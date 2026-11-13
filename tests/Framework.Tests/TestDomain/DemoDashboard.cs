using Framework.Dashboards;
using Framework.Security;
using Framework.Web;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Tests.TestDomain;

/// <summary>Widget contoh untuk data demo (hanya bila dashboard aktif dan tenant belum punya widget).</summary>
public static class DemoDashboard
{
    public static async Task SeedAsync(IServiceProvider sp)
    {
        var dash = sp.GetRequiredService<DashboardService>();
        if (!dash.IsAvailable) return;
        var admin = await sp.GetRequiredService<IUserDirectory>().FindAsync("admin");
        if (admin is null || (await dash.ListAsync(admin)).Count > 0) return;

        var range = new List<WidgetParameter>
        {
            new() { Name = "StartDate", Label = "Dari", Type = ParamTypes.Date, DefaultValue = $"{DateTime.Today.Year}-01-01", Binding = ParamBindings.GlobalStart },
            new() { Name = "EndDate", Label = "Sampai", Type = ParamTypes.Date, DefaultValue = $"{DateTime.Today.Year}-12-31", Binding = ParamBindings.GlobalEnd },
        };
        var dept = new WidgetParameter { Name = "Dept", Label = "Departemen", Type = ParamTypes.Text, Binding = ParamBindings.GlobalDepartment };

        var items = new List<DashboardItem>();

        async Task Add(string name, string desc, string sql, List<WidgetParameter> ps, VisualConfig v, int span, List<string>? roles = null)
        {
            var w = await dash.SaveAsync(admin, new WidgetDef
            {
                Name = name,
                Description = desc,
                Config = new WidgetConfig { Sql = sql, Parameters = ps, Visual = v, Roles = roles ?? [] }
            }, pin: false);
            items.Add(new DashboardItem { WidgetId = w.Id, Span = span });
        }

        await Add("Total pengajuan disetujui", "Nilai pengajuan berstatus Disetujui pada periode filter",
            """
            SELECT COALESCE(SUM(total), 0) AS total_disetujui
            FROM reporting.purchase_request
            WHERE workflow_state = @Status AND request_date BETWEEN @StartDate AND @EndDate
              AND (@Dept IS NULL OR department = @Dept)
            """,
            [new() { Name = "Status", Label = "Status", Type = ParamTypes.Text, DefaultValue = "Disetujui" }, .. range, dept],
            new VisualConfig { Type = ChartTypes.Number, YColumns = ["total_disetujui"] }, 1, ["Finance", "Manager"]);

        await Add("Pengajuan per departemen", "Jumlah pengajuan per departemen",
            """
            SELECT department AS departemen, COUNT(*) AS jumlah
            FROM reporting.purchase_request
            WHERE request_date BETWEEN @StartDate AND @EndDate
            GROUP BY department ORDER BY jumlah DESC
            """,
            [.. range], new VisualConfig { Type = ChartTypes.Doughnut, XColumn = "departemen", YColumns = ["jumlah"] }, 1);

        await Add("Karyawan per departemen", "Karyawan aktif menurut struktur organisasi",
            """
            SELECT d.department_name AS departemen, COUNT(e.name) AS karyawan
            FROM reporting.department d
            LEFT JOIN reporting.employee e ON e.department = d.name AND e.active
            GROUP BY d.department_name ORDER BY karyawan DESC
            """,
            [], new VisualConfig { Type = ChartTypes.Pie, XColumn = "departemen", YColumns = ["karyawan"] }, 1);

        await Add("Nilai pengajuan per status", "Total nilai dan jumlah pengajuan per status workflow",
            """
            SELECT COALESCE(workflow_state, 'Draft') AS status, SUM(total) AS nilai
            FROM reporting.purchase_request
            WHERE request_date BETWEEN @StartDate AND @EndDate AND (@Dept IS NULL OR department = @Dept)
            GROUP BY 1 ORDER BY nilai DESC
            """,
            [.. range, dept], new VisualConfig { Type = ChartTypes.Bar, XColumn = "status", YColumns = ["nilai"] }, 2);

        await Add("Tren pengajuan harian", "Jumlah pengajuan per tanggal",
            """
            SELECT request_date AS tanggal, COUNT(*) AS jumlah
            FROM reporting.purchase_request
            WHERE request_date BETWEEN @StartDate AND @EndDate
            GROUP BY request_date ORDER BY request_date
            """,
            [.. range], new VisualConfig { Type = ChartTypes.Line, XColumn = "tanggal", YColumns = ["jumlah"] }, 1);

        await Add("Barang paling banyak diajukan", "Dari baris item semua pengajuan",
            """
            SELECT i.item_name AS barang, SUM(i.qty) AS jumlah, SUM(i.amount) AS nilai
            FROM reporting.purchase_request_items i
            JOIN reporting.purchase_request p ON p.name = i.parent
            WHERE p.request_date BETWEEN @StartDate AND @EndDate
            GROUP BY i.item_name ORDER BY nilai DESC
            """,
            [.. range], new VisualConfig { Type = ChartTypes.Table }, 3);

        await dash.SaveDashboardAsync(admin, new DashboardDef
        {
            Id = DashboardDef.DefaultDashboardId,
            Name = "Main Dashboard",
            Description = "Default shared overview of procurement requests, approvals, and HR headcount",
            IsShared = true,
            IsDefault = true,
            Layout = new DashboardLayout { Items = items }
        });
    }
}
