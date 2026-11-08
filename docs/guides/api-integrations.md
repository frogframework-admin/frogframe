# API Integrations: API Keys & Custom Methods

FrogFramework provides secure external integration endpoints for automation, third-party services, and microservices.

---

## 1. Integration API Keys

API Keys are managed by System Managers under **API Keys** (`/admin/api-keys`).

```bash
curl -H "Authorization: ApiKey dfk_k1a2b3....<secret>" \
     http://localhost:5080/api/data/PurchaseRequest
```

### Security Properties
- **Account Association**: Every API key binds to an account. It inherits the tenant, roles, and department of that account. Dedicated headless service accounts (with no interactive passwords) are recommended.
- **Granular Scopes**: Enforce least privilege with wildcard segments:
  - `doc:{DocType}:{list|read|create|update|delete|action}`
  - `method:{name}`
  - `query:{slug}`
  - `report:{id}`
- **One-Time Secret Display**: Secrets (256-bit entropy) are shown only once upon generation. Only cryptographically salted SHA-256 hashes are stored.
- **Fail-Closed Endpoints**: Endpoints that do not explicitly declare an integration scope reject API keys by default.

---

## 2. Custom Methods (`IApiMethod`)

For business logic that does not conform to single-document CRUD, implement custom RPC methods implementing `IApiMethod`.

### Defining an API Method

```csharp
public sealed class RemainingBudgetMethod : IApiMethod
{
    public string Name => "remaining-budget";
    public IReadOnlyList<string> Roles => ["Finance", "Manager"];
    
    public IReadOnlyList<ApiMethodParam> Parameters =>
    [
        new("department", ArgTypes.Text, Required: true),
        new("fiscal_year", ArgTypes.Number, Required: false)
    ];

    public async Task<object?> InvokeAsync(ApiMethodContext ctx, CancellationToken ct)
    {
        // Parameter validation is completed before InvokeAsync runs.
        var dept = ctx.GetString("department");
        var year = ctx.GetInt32("fiscal_year") ?? DateTime.UtcNow.Year;

        // Execute queries using DocumentService to respect tenant boundaries
        return new
        {
            department = dept,
            fiscal_year = year,
            remaining_balance = 371_000_000m
        };
    }
}
```

Register the method in the DI container:
```csharp
builder.Services.AddApiMethod<RemainingBudgetMethod>();
```

### Invoking via HTTP

```bash
curl -X POST -H "Authorization: ApiKey dfk_..." \
     -H "Content-Type: application/json" \
     -d '{"department":"IT","fiscal_year":2026}' \
     http://localhost:5080/api/method/remaining-budget
```

