namespace Framework.Web;

/// <summary>Satu kotak di bagan organisasi (OrgTree).</summary>
public sealed record OrgNode(string Id, string? ParentId, string Title, string? Subtitle = null, string? Meta = null,
    string? ImageFileId = null, string? Href = null, bool Highlight = false);
