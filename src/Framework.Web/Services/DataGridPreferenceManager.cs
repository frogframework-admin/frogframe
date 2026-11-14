using System.Globalization;
using System.Reflection;
using System.Text;
using BlazorBlueprint.Primitives.DataGrid;
using Framework.Imports;
using Framework.Preferences;
using Framework.Reports;
using Framework.Security;

namespace Framework.Web.Services;

public sealed record GridExportColumn<TData>(string Id, string Title, Func<TData, object?> Accessor);

public sealed record AdminBulkEditField(
    string Name,
    string Label,
    string EditorType = "text",
    IReadOnlyList<BlazorBlueprint.Primitives.SelectOption<string>>? Options = null,
    string? Placeholder = null,
    string? DefaultValue = null);

/// <summary>
/// Manages per-user, per-table DataGrid column layout preferences (width, visibility),
/// row selection state for bulk actions, and Excel (.xlsx) / CSV (.csv) list exports.
/// </summary>
public sealed class DataGridPreferenceManager<TData> : IDisposable where TData : class
{
    private static readonly Func<TData, string> DefaultKeySelector = BuildDefaultKeySelector();

    private readonly IUserPreferenceStore _userPrefs;
    private readonly CurrentUser _current;
    private readonly string _storageKey;
    private readonly Action _stateHasChanged;
    private readonly Func<TData, string> _keySelector;
    private System.Threading.Timer? _debounceTimer;

    public DataGridPreference Preference { get; private set; } = new();
    public bool HasCustomLayout { get; private set; }
    public int RenderKey { get; private set; }

    public HashSet<string> SelectedIds { get; } = new(StringComparer.Ordinal);
    public int SelectedCount => SelectedIds.Count;

    public DataGridPreferenceManager(
        IUserPreferenceStore userPrefs,
        CurrentUser current,
        string storageKey,
        Action stateHasChanged,
        Func<TData, string>? keySelector = null)
    {
        _userPrefs = userPrefs;
        _current = current;
        _storageKey = storageKey;
        _stateHasChanged = stateHasChanged;
        _keySelector = keySelector ?? DefaultKeySelector;
    }

    private static Func<TData, string> BuildDefaultKeySelector()
    {
        var type = typeof(TData);
        var prop = type.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
                   ?? type.GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null)
        {
            return item => prop.GetValue(item)?.ToString() ?? "";
        }
        return item => item?.GetHashCode().ToString(CultureInfo.InvariantCulture) ?? "";
    }

    public string KeyOf(TData item) => _keySelector(item);

    public bool IsSelected(TData item) => SelectedIds.Contains(KeyOf(item));

    public void ToggleSelect(TData item, bool check)
    {
        var key = KeyOf(item);
        if (string.IsNullOrEmpty(key)) return;
        if (check) SelectedIds.Add(key);
        else SelectedIds.Remove(key);
        _stateHasChanged();
    }

    public bool IsAllSelected(IEnumerable<TData>? items)
    {
        if (items is null) return false;
        var list = items as IReadOnlyCollection<TData> ?? items.ToList();
        return list.Count > 0 && list.All(i => SelectedIds.Contains(KeyOf(i)));
    }

    public bool IsSomeSelected(IEnumerable<TData>? items)
    {
        if (items is null) return false;
        var list = items as IReadOnlyCollection<TData> ?? items.ToList();
        return list.Count > 0 && !IsAllSelected(list) && list.Any(i => SelectedIds.Contains(KeyOf(i)));
    }

    public void ToggleSelectAll(IEnumerable<TData>? items, bool check)
    {
        if (items is null) return;
        foreach (var item in items)
        {
            var key = KeyOf(item);
            if (string.IsNullOrEmpty(key)) continue;
            if (check) SelectedIds.Add(key);
            else SelectedIds.Remove(key);
        }
        _stateHasChanged();
    }

    public IReadOnlyList<TData> GetSelectedItems(IEnumerable<TData>? items)
    {
        if (items is null || SelectedIds.Count == 0) return [];
        return items.Where(i => SelectedIds.Contains(KeyOf(i))).ToList();
    }

    public void ClearSelection()
    {
        if (SelectedIds.Count == 0) return;
        SelectedIds.Clear();
        _stateHasChanged();
    }

    public void RetainSelection(IEnumerable<TData>? items)
    {
        if (items is null)
        {
            SelectedIds.Clear();
            return;
        }
        var validKeys = items.Select(KeyOf).ToHashSet(StringComparer.Ordinal);
        SelectedIds.IntersectWith(validKeys);
    }

    public ReportOutput BuildExport(
        string format,
        string filePrefix,
        IEnumerable<TData>? items,
        IReadOnlyList<GridExportColumn<TData>> columns)
    {
        var fmt = (format ?? "xlsx").Trim().ToLowerInvariant();
        if (fmt is not ("xlsx" or "csv"))
            throw new ArgumentException($"Unsupported export format '{format}'. Expected 'xlsx' or 'csv'.", nameof(format));

        var allItems = (items ?? []).ToList();
        var exportItems = SelectedIds.Count > 0
            ? allItems.Where(IsSelected).ToList()
            : allItems;

        var activeCols = columns.Where(c => IsVisible(c.Id)).ToList();
        if (activeCols.Count == 0)
            activeCols = columns.ToList();

        var headers = activeCols.Select(c => c.Title).ToList();
        var rows = new List<object?[]>(exportItems.Count);
        foreach (var item in exportItems)
        {
            var row = new object?[activeCols.Count];
            for (var i = 0; i < activeCols.Count; i++)
            {
                row[i] = NormalizeExportValue(activeCols[i].Accessor(item));
            }
            rows.Add(row);
        }

        var safePrefix = SanitizeFileName(string.IsNullOrWhiteSpace(filePrefix) ? "export" : filePrefix);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);

        if (fmt == "csv")
        {
            var csvBytes = BuildCsvBytes(headers, rows);
            return new ReportOutput(csvBytes, $"{safePrefix}-{stamp}.csv", "text/csv; charset=utf-8", rows.Count);
        }

        var sheetName = safePrefix.Length > 31 ? safePrefix[..31] : safePrefix;
        var xlsxBytes = XlsxWriter.Build([
            new XlsxSheet
            {
                Name = string.IsNullOrWhiteSpace(sheetName) ? "Data" : sheetName,
                Headers = headers,
                Rows = rows,
            }
        ]);
        return new ReportOutput(xlsxBytes, $"{safePrefix}-{stamp}.xlsx", ReportFormats.ContentType(ReportFormats.Xlsx), rows.Count);
    }

    private static object? NormalizeExportValue(object? raw) => raw switch
    {
        null => null,
        DateTimeOffset dto => dto.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "Yes" : "No",
        int i => (long)i,
        long l => l,
        decimal dec => dec,
        double dbl => (decimal)dbl,
        float flt => (decimal)flt,
        IEnumerable<string> strList => SanitizeFormulaInjection(string.Join(", ", strList)),
        _ => SanitizeFormulaInjection(Convert.ToString(raw, CultureInfo.InvariantCulture) ?? "")
    };

    private static string SanitizeFormulaInjection(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var first = value[0];
        return first is '=' or '+' or '-' or '@' ? "'" + value : value;
    }

    private static byte[] BuildCsvBytes(IReadOnlyList<string> headers, IReadOnlyList<object?[]> rows)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < headers.Count; i++)
        {
            if (i > 0) sb.Append(',');
            WriteCsvCell(sb, SanitizeFormulaInjection(headers[i]));
        }
        sb.Append("\r\n");

        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var text = row[i] switch
                {
                    null => "",
                    decimal d => d.ToString(CultureInfo.InvariantCulture),
                    long l => l.ToString(CultureInfo.InvariantCulture),
                    _ => Convert.ToString(row[i], CultureInfo.InvariantCulture) ?? ""
                };
                WriteCsvCell(sb, text);
            }
            sb.Append("\r\n");
        }

        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        return utf8Bom.GetBytes(sb.ToString());
    }

    private static void WriteCsvCell(StringBuilder sb, string value)
    {
        if (string.IsNullOrEmpty(value)) return;
        if (value.IndexOfAny([',', '"', '\r', '\n']) >= 0)
        {
            sb.Append('"');
            sb.Append(value.Replace("\"", "\"\"", StringComparison.Ordinal));
            sb.Append('"');
        }
        else
        {
            sb.Append(value);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (Array.IndexOf(invalid, ch) < 0 && !char.IsWhiteSpace(ch))
                sb.Append(ch);
            else if (char.IsWhiteSpace(ch))
                sb.Append('-');
        }
        return sb.Length > 0 ? sb.ToString().ToLowerInvariant() : "export";
    }

    public async Task InitializeAsync()
    {
        if (_current.User is { } u)
        {
            var saved = await _userPrefs.GetAsync<DataGridPreference>(u.TenantId, u.Id, _storageKey);
            if (saved is not null && saved.Columns.Count > 0)
            {
                Preference = saved;
                HasCustomLayout = true;
                _stateHasChanged();
            }
        }
    }

    public string? GetWidth(string colId, string? defaultWidth = null)
    {
        if (colId is "actions" or "action" or "_select" && defaultWidth is not null) return defaultWidth;
        return Preference.Columns.TryGetValue(colId, out var pref) && !string.IsNullOrWhiteSpace(pref.Width)
            ? pref.Width
            : defaultWidth;
    }

    public bool IsVisible(string colId) =>
        !Preference.Columns.TryGetValue(colId, out var pref) || pref.Visible;

    public void HandleResize((string ColumnId, string Width) args)
    {
        if (string.IsNullOrWhiteSpace(args.ColumnId) || args.ColumnId is "actions" or "action" or "_select") return;
        if (!Preference.Columns.TryGetValue(args.ColumnId, out var pref))
        {
            pref = new GridColumnPreference { Visible = true };
        }
        Preference.Columns[args.ColumnId] = pref with { Width = args.Width };
        HasCustomLayout = true;
        ScheduleSave();
    }

    public void HandleStateChanged(DataGridState<TData> state)
    {
        if (state?.Columns?.Entries is null) return;
        bool changed = false;
        foreach (var entry in state.Columns.Entries)
        {
            if (string.IsNullOrEmpty(entry.ColumnId) || entry.ColumnId is "actions" or "action" or "_select") continue;
            if (!Preference.Columns.TryGetValue(entry.ColumnId, out var pref))
            {
                if (!entry.Visible || !string.IsNullOrWhiteSpace(entry.Width))
                {
                    Preference.Columns[entry.ColumnId] = new GridColumnPreference
                    {
                        Visible = entry.Visible,
                        Width = entry.Width
                    };
                    changed = true;
                }
            }
            else
            {
                var widthToSave = !string.IsNullOrWhiteSpace(entry.Width) ? entry.Width : pref.Width;
                if (pref.Visible != entry.Visible || pref.Width != widthToSave)
                {
                    Preference.Columns[entry.ColumnId] = pref with { Visible = entry.Visible, Width = widthToSave };
                    changed = true;
                }
            }
        }

        if (changed)
        {
            HasCustomLayout = true;
            ScheduleSave();
            _stateHasChanged();
        }
    }

    public async Task ResetAsync()
    {
        if (_current.User is { } u)
        {
            await _userPrefs.DeleteAsync(u.TenantId, u.Id, _storageKey);
        }
        Preference = new();
        HasCustomLayout = false;
        RenderKey++;
        _stateHasChanged();
    }

    private void ScheduleSave()
    {
        _debounceTimer?.Dispose();
        _debounceTimer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (_current.User is { } u)
                {
                    await _userPrefs.SetAsync(u.TenantId, u.Id, _storageKey, Preference);
                }
            }
            catch
            {
                // Background save failure shouldn't crash UI
            }
        }, null, 500, System.Threading.Timeout.Infinite);
    }

    public void Dispose()
    {
        _debounceTimer?.Dispose();
    }
}
