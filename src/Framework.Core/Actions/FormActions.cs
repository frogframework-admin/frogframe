using Framework.Documents;
using Framework.Metadata;
using Framework.Security;

namespace Framework.Actions;

public interface IFormAction
{
    string DocType { get; }
    string ActionId { get; }
    string Label { get; }
    string Icon => "play";
    string Tone => "neutral"; // default/neutral, positive, negative, pending
    bool IsVisible(UserContext user, Document doc);
    Task<FormActionResult> ExecuteAsync(FormActionContext context, CancellationToken ct);
}

public interface IFormAction<TDoc> : IFormAction
{
    string IFormAction.DocType => typeof(TDoc).Name;
}

public sealed class FormActionContext
{
    public required UserContext User { get; init; }
    public required CompiledDocType Meta { get; init; }
    public required Document Doc { get; init; }
    public required DocumentService Docs { get; init; }
    public IServiceProvider Services { get; init; } = default!;
}

public sealed record FormActionResult
{
    public bool Success { get; init; } = true;
    public string? Message { get; init; }
    public string? RedirectUrl { get; init; }
    public bool RefreshDocument { get; init; } = true;

    public static FormActionResult Ok(string? message = null, bool refresh = true) =>
        new() { Success = true, Message = message, RefreshDocument = refresh };

    public static FormActionResult NavigateTo(string url, string? message = null) =>
        new() { Success = true, Message = message, RedirectUrl = url };

    public static FormActionResult Fail(string message) =>
        new() { Success = false, Message = message };
}

