using Framework.Documents;
using Framework.Metadata;
using Framework.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Web;

/// <summary>
/// A Blazor component an application shows on the form of one DocType, above the tabs (for example a review panel that
/// gathers what an approver needs to decide). The component gets two parameters: <c>Doc</c> (<see cref="Document"/>,
/// the saved document) and <c>Meta</c> (<see cref="CompiledDocType"/>). It is rendered for saved documents only, after
/// the form has checked the user may read the document, and it is rebuilt when the document is reloaded (after a save
/// or a workflow action).
/// </summary>
public sealed record FormPanel(string DocType, Type Component, int Order = 100, Func<UserContext, Document, bool>? Visible = null);

public static class FormPanelServiceCollectionExtensions
{
    /// <summary>Adds a panel to the form of <paramref name="docType"/>. <paramref name="visible"/> decides per user and document.</summary>
    public static IServiceCollection AddFormPanel<TComponent>(this IServiceCollection services, string docType, int order = 100,
        Func<UserContext, Document, bool>? visible = null) where TComponent : IComponent
    {
        services.AddSingleton(new FormPanel(docType, typeof(TComponent), order, visible));
        return services;
    }

    /// <summary>The panels to render for this document and user, in order.</summary>
    public static IReadOnlyList<FormPanel> For(this IEnumerable<FormPanel> panels, UserContext user, Document doc) =>
        panels.Where(p => string.Equals(p.DocType, doc.DocType, StringComparison.Ordinal) && (p.Visible?.Invoke(user, doc) ?? true))
              .OrderBy(p => p.Order).ToList();
}
