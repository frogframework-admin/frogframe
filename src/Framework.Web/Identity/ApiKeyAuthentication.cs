using System.Security.Claims;
using System.Text.Encodings.Web;
using Framework.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Framework.Web.Identity;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string KeyIdClaim = "frogframe:api_key";
    public const string ScopeClaim = "frogframe:api_scope";
}

/// <summary>
/// Autentikasi "Authorization: ApiKey dfk_…". Klaim dibuat oleh pabrik klaim yang sama dengan login (tenant, role,
/// departemen dari akun pemilik kunci), ditambah id kunci dan cakupannya.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(ApiKeyDefaults.Scheme + " ", StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var verified = await keys.VerifyAsync(header[(ApiKeyDefaults.Scheme.Length + 1)..].Trim(), Context.Connection.RemoteIpAddress, Context.RequestAborted);
        if (verified is null) return AuthenticateResult.Fail("Kunci API tidak valid, dicabut, kedaluwarsa, atau IP tidak diizinkan");
        var principal = await Context.RequestServices.GetRequiredService<IUserClaimsPrincipalFactory<UserAccount>>().CreateAsync(verified.Account);
        var id = (ClaimsIdentity)principal.Identity!;
        id.AddClaim(new Claim(ApiKeyDefaults.KeyIdClaim, verified.Key.Id));
        foreach (var s in verified.Key.Scopes.DefaultIfEmpty("*")) id.AddClaim(new Claim(ApiKeyDefaults.ScopeClaim, s));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Task.CompletedTask;
    }
}
