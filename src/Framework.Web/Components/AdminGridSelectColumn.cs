using BlazorBlueprint.Components;
using BlazorBlueprint.Primitives.DataGrid;
using Framework.Localization;
using Framework.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Framework.Web.Components;

/// <summary>
/// Checkbox selection column placed as the first (leftmost) column in admin DataGrids.
/// Inherits directly from <see cref="BbDataGridTemplateColumn{TData}"/> so that it registers
/// at column index 0 (Order 0) rather than being appended at the end of the grid.
/// </summary>
public class AdminGridSelectColumn<TData> : BbDataGridTemplateColumn<TData> where TData : class
{
    [Parameter, EditorRequired] public DataGridPreferenceManager<TData> Grid { get; set; } = default!;
    [Parameter] public IEnumerable<TData>? Items { get; set; }
    [Inject] public UiText T { get; set; } = default!;

    public AdminGridSelectColumn()
    {
        Id = "_select";
        base.Title = "";
        Width = "3rem";
        Hideable = false;
        Resizable = false;
        Reorderable = false;
        Sortable = false;
        Filterable = false;
        Groupable = false;
        Order = 0;
        Pinned = ColumnPinning.None;
    }

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);

        Id = "_select";
        base.Title = "";
        Width = "3rem";
        Hideable = false;
        Resizable = false;
        Reorderable = false;
        Sortable = false;
        Filterable = false;
        Groupable = false;
        Order = 0;
        Pinned = ColumnPinning.None;

        HeaderTemplate = (RenderTreeBuilder builder) =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "style", "display:flex;align-items:center;justify-content:center;");
            builder.AddAttribute(2, "onclick:stopPropagation", true);

            builder.OpenComponent<BbCheckbox>(3);
            builder.AddAttribute(4, "Checked", Grid.IsAllSelected(Items));
            builder.AddAttribute(5, "Indeterminate", Grid.IsSomeSelected(Items));
            builder.AddAttribute(6, "CheckedChanged", EventCallback.Factory.Create<bool>(this, v => Grid.ToggleSelectAll(Items, v)));
            builder.AddAttribute(7, "AriaLabel", T["Select all"]);
            builder.CloseComponent();

            builder.CloseElement();
        };

        ChildContent = (TData item) => (RenderTreeBuilder builder) =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "style", "display:flex;align-items:center;justify-content:center;");
            builder.AddAttribute(2, "onclick:stopPropagation", true);

            builder.OpenComponent<BbCheckbox>(3);
            builder.AddAttribute(4, "Checked", Grid.IsSelected(item));
            builder.AddAttribute(5, "CheckedChanged", EventCallback.Factory.Create<bool>(this, v => Grid.ToggleSelect(item, v)));
            builder.AddAttribute(6, "AriaLabel", T.Format("Select {0}", Grid.KeyOf(item)));
            builder.CloseComponent();

            builder.CloseElement();
        };
    }
}
