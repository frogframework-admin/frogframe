using BlazorBlueprint.Primitives.DataGrid;
using Framework.Preferences;
using Framework.Security;

namespace Framework.Web.Services;

/// <summary>
/// Mengelola preferensi tata letak kolom DataGrid (lebar, visibilitas, urutan)
/// yang disimpan terisolasi per-user dan per-tabel dengan debounced auto-save.
/// </summary>
public sealed class DataGridPreferenceManager<TData> : IDisposable where TData : class
{
    private readonly IUserPreferenceStore _userPrefs;
    private readonly CurrentUser _current;
    private readonly string _storageKey;
    private readonly Action _stateHasChanged;
    private System.Threading.Timer? _debounceTimer;

    public DataGridPreference Preference { get; private set; } = new();
    public bool HasCustomLayout { get; private set; }
    public int RenderKey { get; private set; }

    public DataGridPreferenceManager(IUserPreferenceStore userPrefs, CurrentUser current, string storageKey, Action stateHasChanged)
    {
        _userPrefs = userPrefs;
        _current = current;
        _storageKey = storageKey;
        _stateHasChanged = stateHasChanged;
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
        if (colId == "actions" && defaultWidth is not null) return defaultWidth;
        return Preference.Columns.TryGetValue(colId, out var pref) && !string.IsNullOrWhiteSpace(pref.Width)
            ? pref.Width
            : defaultWidth;
    }

    public bool IsVisible(string colId) =>
        !Preference.Columns.TryGetValue(colId, out var pref) || pref.Visible;

    public void HandleResize((string ColumnId, string Width) args)
    {
        if (string.IsNullOrWhiteSpace(args.ColumnId) || args.ColumnId == "actions") return;
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
            if (string.IsNullOrEmpty(entry.ColumnId) || entry.ColumnId == "actions") continue;
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
