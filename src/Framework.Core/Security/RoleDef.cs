namespace Framework.Security;

public sealed class RoleDef
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "default";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystem { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

