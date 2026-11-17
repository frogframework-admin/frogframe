using Microsoft.Extensions.Configuration;

namespace Framework.Web;

/// <summary>
/// Configuration section <c>Ui</c> controlling UI pagination defaults.
/// </summary>
public sealed class UiOptions
{
    public const string SectionName = "Ui";

    /// <summary>Number of mention items loaded per page on <c>/mentions</c>.</summary>
    public int MentionsPageSize { get; set; } = 20;

    /// <summary>Number of task items loaded per page on <c>/tasks</c>.</summary>
    public int TasksPageSize { get; set; } = 20;

    /// <summary>Number of entries loaded per page in the Form History &amp; Comments tab.</summary>
    public int HistoryCommentsPageSize { get; set; } = 10;

    public static UiOptions From(IConfiguration? configuration)
    {
        var options = new UiOptions();
        configuration?.GetSection(SectionName).Bind(options);
        if (options.MentionsPageSize < 1) options.MentionsPageSize = 20;
        if (options.TasksPageSize < 1) options.TasksPageSize = 20;
        if (options.HistoryCommentsPageSize < 1) options.HistoryCommentsPageSize = 10;
        return options;
    }
}
