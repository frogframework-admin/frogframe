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
/// at column index 0 (Pinned Left, Order 0) rather than being appended at the end of the grid.
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
        Order = 0;
        Pinned = ColumnPinning.Left;
    }

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        await base.SetParametersAsync(parameters);

        Id = "_select";
        base.Title = "";
        Width = "3rem";
        Hideable = false;
        Resizable = false;
        Order = 0;
        Pinned = ColumnPinning.Left;

        HeaderTemplate = (RenderTreeBuilder builder) =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "onclick:stopPropagation", true);

            builder.OpenComponent<BbCheckbox>(2);
            builder.AddAttribute(3, "Checked", Grid.IsAllSelected(Items));
            builder.AddAttribute(4, "Indeterminate", Grid.IsSomeSelected(Items));
            builder.AddAttribute(5, "CheckedChanged", EventCallback.Factory.Create<bool>(this, v => Grid.ToggleSelectAll(Items, v)));
            builder.AddAttribute(6, "AriaLabel", T["Select all"]);
            builder.CloseComponent();

            builder.CloseElement();
        };

        ChildContent = (TData item) => (RenderTreeBuilder builder) =>
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "onclick:stopPropagation", true);

            builder.OpenComponent<BbCheckbox>(2);
            builder.AddAttribute(3, "Checked", Grid.IsSelected(item));
            builder.AddAttribute(4, "CheckedChanged", EventCallback.Factory.Create<bool>(this, v => Grid.ToggleSelect(item, v)));
            builder.AddAttribute(5, "AriaLabel", T.Format("Select {0}", Grid.KeyOf(item)));
            builder.CloseComponent();

            builder.CloseElement();
        };
    }
}
