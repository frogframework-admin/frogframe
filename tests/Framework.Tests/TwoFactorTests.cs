using System.Security.Claims;
using System.Security.Cryptography;
using Framework.Persistence.PostgreSql;
using Framework.Security;
using Framework.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// P2-1: two-factor authentication (authenticator app + recovery codes) and single sign-on (Google / Entra ID).
/// </summary>
sealed partial class Tests
{
    private static ServiceProvider BuildIdentityProvider(Action<IServiceCollection>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddFrogFrameworkIdentity();
        extra?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>A scope with an HttpContext, as SignInManager needs one.</summary>
    private static (AsyncServiceScope Scope, DefaultHttpContext Http) IdentityRequest(IServiceProvider sp, string? cookies = null)
    {
        var scope = sp.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("frogframe.test");
        if (cookies is not null) http.Request.Headers.Cookie = cookies;
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;
        return (scope, http);
    }

    private static async Task<UserAccount> CreateIdentityUserAsync(UserManager<UserAccount> users, string name, string? email = null, params string[] roles)
    {
        var account = new UserAccount { UserName = name, FullName = name, TenantId = "acme", Email = email, Roles = [.. roles] };
        var result = await users.CreateAsync(account, "Correct-Horse-42");
        Assert.True(result.Succeeded, "user created: " + string.Join(", ", result.Errors.Select(e => e.Description)));
        return (await users.FindByNameAsync(name))!;
    }

    /// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits), as an authenticator app computes it.</summary>
    private static string Totp(string base32Key, DateTimeOffset? at = null, int stepOffset = 0)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(base32Key.ToUpperInvariant().Where(c => c != '=').Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        var key = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30 + stepOffset;
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        var hash = HMACSHA1.HashData(key, msg);
        var o = hash[^1] & 0x0F;
        var code = ((hash[o] & 0x7F) << 24 | hash[o + 1] << 16 | hash[o + 2] << 8 | hash[o + 3]) % 1_000_000;
        return code.ToString("D6");
    }

    private static async Task<(string Key, IReadOnlyList<string> Codes)> EnrolAsync(UserManager<UserAccount> users, UserAccount user)
    {
        await users.ResetAuthenticatorKeyAsync(user);
        var key = (await users.GetAuthenticatorKeyAsync(user))!;
        Assert.True(await TwoFactorSetup.VerifyAuthenticatorCodeAsync(users, user, Totp(key)), "code from the app verifies before enabling");
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToList();
        return (key, codes);
    }

    [Test]
    public async Task Two_factor_secret_is_encrypted_and_recovery_codes_are_hashed_and_single_use()
    {
        await using var sp = BuildIdentityProvider();
        var (scope, _) = IdentityRequest(sp);
        await using var _s = scope;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var repo = scope.ServiceProvider.GetRequiredService<IUserAccountRepository>();
        var user = await CreateIdentityUserAsync(users, "tf-store");

        var stampBefore = user.SecurityStamp;
        var (key, codes) = await EnrolAsync(users, user);
        Assert.True(key.Length >= 26 && key.All(c => char.IsAsciiLetterUpper(c) || c is >= '2' and <= '7'), "authenticator key is base32");
        Assert.True(user.SecurityStamp != stampBefore, "enabling two-factor changes the security stamp (other sessions end)");

        var stored = (await repo.FindByIdAsync(user.Id))!;
        Assert.True(stored.TwoFactorEnabled, "enabled flag stored");
        Assert.True(stored.AuthenticatorKey is { Length: > 0 } && !stored.AuthenticatorKey.Contains(key), "secret stored encrypted, not in plain text");
        Assert.Equal(10, stored.RecoveryCodes.Count, "ten recovery codes stored");
        Assert.True(codes.All(c => !stored.RecoveryCodes.Contains(c) && !stored.RecoveryCodes.Contains(c.Replace("-", ""))), "recovery codes stored only as hashes");

        Assert.True(!await TwoFactorSetup.VerifyAuthenticatorCodeAsync(users, user, Totp(key, stepOffset: 20)), "a code from 10 minutes ahead is rejected");
        Assert.True(!await TwoFactorSetup.VerifyAuthenticatorCodeAsync(users, user, "000000") || Totp(key) == "000000", "a wrong code is rejected");

        // Redeem in a different form (lower case, no dash): works once only.
        var first = codes[0].Replace("-", "").ToLowerInvariant();
        Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(user, first)).Succeeded, "recovery code accepted");
        Assert.True(!(await users.RedeemTwoFactorRecoveryCodeAsync(user, codes[0])).Succeeded, "recovery code rejected the second time");
        Assert.Equal(9, await users.CountRecoveryCodesAsync(user), "one code used");
        Assert.Equal(9, (await repo.FindByIdAsync(user.Id))!.RecoveryCodes.Count, "redeemed code removed from storage");

        Assert.Equal("Authenticator", string.Join(",", await users.GetValidTwoFactorProvidersAsync(user)), "only the authenticator app is a second factor");

        // Lost Data Protection keys: the secret cannot be decrypted. Still two-factor (fail closed), but app codes fail.
        stored = (await repo.FindByIdAsync(user.Id))!;
        stored.AuthenticatorKey = "not-a-protected-payload";
        await repo.UpdateAsync(stored);
        var reloaded = (await users.FindByIdAsync(user.Id))!;
        Assert.True(await users.GetAuthenticatorKeyAsync(reloaded) is { } lost && lost != key, "unreadable secret is not returned");
        Assert.True((await users.GetValidTwoFactorProvidersAsync(reloaded)).Count == 1, "account still requires a second factor");
        Assert.True(!await TwoFactorSetup.VerifyAuthenticatorCodeAsync(users, reloaded, Totp(key)), "app codes fail without the secret");

        // Turning it off forgets everything and ends other sessions.
        var stamp = reloaded.SecurityStamp;
        Assert.True((await TwoFactorSetup.DisableAsync(users, reloaded)).Succeeded, "disable succeeded");
        var off = (await repo.FindByIdAsync(user.Id))!;
        Assert.True(!off.TwoFactorEnabled && off.AuthenticatorKey is null && off.RecoveryCodes.Count == 0, "disable clears flag, secret and codes");
        Assert.True(off.SecurityStamp != stamp, "disable changes the security stamp");
    }

    [Test]
    public async Task Two_factor_login_needs_the_code_after_the_password()
    {
        await using var sp = BuildIdentityProvider();
        string key;
        IReadOnlyList<string> codes;
        {
            var (scope, _) = IdentityRequest(sp);
            await using var _s = scope;
            var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
            (key, codes) = await EnrolAsync(users, await CreateIdentityUserAsync(users, "tf-login"));
        }

        // Browser: step 1 sets only the short-lived Identity.TwoFactorUserId cookie, not the application cookie.
        string pendingCookie;
        {
            var (scope, http) = IdentityRequest(sp);
            await using var _s = scope;
            var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<UserAccount>>();
            var result = await signIn.PasswordSignInAsync("tf-login", "Correct-Horse-42", false, lockoutOnFailure: true);
            Assert.True(result.RequiresTwoFactor && !result.Succeeded, "password alone is not enough");
            var setCookies = http.Response.Headers.SetCookie.ToString();
            Assert.True(setCookies.Contains("Identity.TwoFactorUserId") && !setCookies.Contains("frogframe.auth="), "only the pending two-factor cookie is issued");
            pendingCookie = string.Join("; ", http.Response.Headers.SetCookie.Select(c => c!.Split(';')[0]));
        }

        // Step 2 (next request, as /account/login-2fa): wrong code fails and counts as a failed attempt; right code signs in.
        {
            var (scope, http) = IdentityRequest(sp, pendingCookie);
            await using var _s = scope;
            var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<UserAccount>>();
            var pending = await signIn.GetTwoFactorAuthenticationUserAsync();
            Assert.Equal("tf-login", pending?.UserName, "pending user found from the cookie");
            var wrong = Totp(key, stepOffset: 20);
            Assert.True(!(await signIn.TwoFactorAuthenticatorSignInAsync(wrong, false, false)).Succeeded, "wrong code refused");
            Assert.Equal(1, (await signIn.UserManager.FindByNameAsync("tf-login"))!.AccessFailedCount, "wrong code counts towards lockout");
        }
        {
            var (scope, http) = IdentityRequest(sp, pendingCookie);
            await using var _s = scope;
            var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<UserAccount>>();
            var ok = await signIn.TwoFactorAuthenticatorSignInAsync(Totp(key), false, false);
            Assert.True(ok.Succeeded, "right code signs in");
            Assert.True(http.Response.Headers.SetCookie.ToString().Contains("frogframe.auth="), "application cookie issued after the code");
        }

        // API style: password and recovery code in the same request (what POST /api/auth/login does).
        {
            var (scope, _) = IdentityRequest(sp);
            await using var _s = scope;
            var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<UserAccount>>();
            Assert.True((await signIn.PasswordSignInAsync("tf-login", "Correct-Horse-42", false, true)).RequiresTwoFactor, "API: two-factor required");
            Assert.True((await signIn.TwoFactorRecoveryCodeSignInAsync(codes[3])).Succeeded, "API: recovery code completes the sign-in in the same request");
        }
    }

    [Test]
    public async Task Two_factor_requirement_by_role_redirects_until_enrolled()
    {
        await using var sp = BuildIdentityProvider(s => s.Configure<TwoFactorOptions>(o => o.RequiredForRoles = ["Accounts Manager, System Manager"]));
        var (scope, _) = IdentityRequest(sp);
        await using var _s = scope;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var factory = scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<UserAccount>>();
        var options = sp.GetRequiredService<IOptionsMonitor<TwoFactorOptions>>();
        Assert.Equal(2, options.CurrentValue.EffectiveRequiredRoles.Count, "comma-separated roles are split");

        async Task<(bool NextCalled, int Status, string? Location)> Run(ClaimsPrincipal principal, string path, string? auth = null)
        {
            var called = false;
            var mw = new TwoFactorRequirementMiddleware(_ => { called = true; return Task.CompletedTask; }, options);
            var ctx = new DefaultHttpContext { User = principal };
            ctx.Request.Path = path;
            ctx.Response.Body = new MemoryStream();
            await mw.InvokeAsync(ctx);
            return (called, ctx.Response.StatusCode, ctx.Response.Headers.Location.ToString());
        }

        var manager = await CreateIdentityUserAsync(users, "tf-manager", null, "System Manager");
        var clerk = await CreateIdentityUserAsync(users, "tf-clerk", null, "Employee");
        var managerPrincipal = await factory.CreateAsync(manager);

        var page = await Run(managerPrincipal, "/app/sales-invoice");
        Assert.True(!page.NextCalled && page.Status == 302 && page.Location == "/account/two-factor?required=1", "manager without 2FA is sent to enrolment");
        Assert.Equal(403, (await Run(managerPrincipal, "/api/v1/data/Customer")).Status, "API refuses with 403");
        Assert.True((await Run(managerPrincipal, "/account/two-factor")).NextCalled, "enrolment page reachable");
        Assert.True((await Run(managerPrincipal, "/_content/app.css")).NextCalled, "static files reachable");
        Assert.True((await Run(await factory.CreateAsync(clerk), "/app/sales-invoice")).NextCalled, "roles not listed are not affected");
        Assert.True((await Run(new ClaimsPrincipal(new ClaimsIdentity()), "/app")).NextCalled, "anonymous requests pass (login handles them)");

        var apiKeyPrincipal = await factory.CreateAsync(manager);
        ((ClaimsIdentity)apiKeyPrincipal.Identity!).AddClaim(new Claim(ApiKeyDefaults.KeyIdClaim, "k1"));
        Assert.True((await Run(apiKeyPrincipal, "/api/v1/data/Customer")).NextCalled, "API keys (service accounts) are not affected");

        var sso = await factory.CreateAsync(manager);
        ((ClaimsIdentity)sso.Identity!).AddClaim(new Claim("amr", TwoFactorSetup.SsoMethodPrefix + SingleSignOn.Microsoft));
        Assert.True((await Run(sso, "/app")).NextCalled, "single sign-on satisfies the rule (TrustSingleSignOn default)");
        options.CurrentValue.TrustSingleSignOn = false;
        Assert.True(!(await Run(sso, "/app")).NextCalled, "unless TrustSingleSignOn is false");
        options.CurrentValue.TrustSingleSignOn = true;

        await EnrolAsync(users, manager);
        var enrolled = await factory.CreateAsync((await users.FindByIdAsync(manager.Id))!);
        Assert.True(enrolled.HasClaim(TwoFactorSetup.EnabledClaim, "1"), "enrolled account gets the claim");
        Assert.True((await Run(enrolled, "/app/sales-invoice")).NextCalled, "enrolled manager passes");

        // The 5-minute security-stamp check keeps how the user signed in.
        var current = new ClaimsPrincipal(new ClaimsIdentity([new Claim("amr", "mfa"), new Claim(ClaimTypes.AuthenticationMethod, "pwd")], "test"));
        var rebuilt = await factory.CreateAsync(manager);
        await Framework.Web.Identity.IdentityServiceCollectionExtensions.PreserveAuthenticationMethod(new SecurityStampRefreshingPrincipalContext { CurrentPrincipal = current, NewPrincipal = rebuilt });
        Assert.True(rebuilt.HasClaim("amr", "mfa") && rebuilt.HasClaim(ClaimTypes.AuthenticationMethod, "pwd"), "amr preserved on refresh");
    }

    [Test]
    public async Task Single_sign_on_matches_existing_accounts_by_verified_email_only()
    {
        IConfiguration Config(params (string Key, string Value)[] values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

        // Nothing configured: no providers, no schemes.
        await using (var none = BuildIdentityProvider(s => s.AddFrogFrameworkSso(Config())))
            Assert.Equal(0, none.GetRequiredService<SsoProviders>().All.Count, "no providers without configuration");

        // Entra must name one directory.
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddFrogFrameworkSso(Config(
            ("Authentication:Microsoft:ClientId", "id"), ("Authentication:Microsoft:ClientSecret", "s"), ("Authentication:Microsoft:TenantId", "common"))),
            "multi-tenant authority refused");

        await using var sp = BuildIdentityProvider(s => s.AddFrogFrameworkSso(Config(
            ("Authentication:Google:ClientId", "gid"), ("Authentication:Google:ClientSecret", "gsecret"),
            ("Authentication:Microsoft:ClientId", "mid"), ("Authentication:Microsoft:ClientSecret", "msecret"),
            ("Authentication:Microsoft:TenantId", "contoso.onmicrosoft.com"),
            ("Authentication:AllowedEmailDomains:0", "acme.co.id, @acme.com"))));
        var providers = sp.GetRequiredService<SsoProviders>();
        Assert.Equal("google,microsoft", string.Join(",", providers.All.Select(p => p.Scheme)), "both providers enabled");
        Assert.Equal("acme.co.id,acme.com", string.Join(",", providers.AllowedEmailDomains), "allowed domains normalised");
        var schemes = sp.GetRequiredService<IAuthenticationSchemeProvider>();
        Assert.True(await schemes.GetSchemeAsync(SingleSignOn.Google) is not null && await schemes.GetSchemeAsync(SingleSignOn.Microsoft) is not null, "schemes registered");
        var entra = sp.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.OAuth.OAuthOptions>>().Get(SingleSignOn.Microsoft);
        Assert.True(entra.AuthorizationEndpoint.Contains("/contoso.onmicrosoft.com/") && entra.UsePkce
            && entra.SignInScheme == IdentityConstants.ExternalScheme, "Entra: single directory, PKCE, external cookie");

        var (scope, _) = IdentityRequest(sp);
        await using var _s = scope;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var account = await CreateIdentityUserAsync(users, "sso-user", "Rina@Acme.co.id");

        ClaimsPrincipal External(string? email, string? verified = null)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "sub-1") };
            if (email is not null) claims.Add(new Claim(ClaimTypes.Email, email));
            if (verified is not null) claims.Add(new Claim("email_verified", verified));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "external"));
        }

        var ok = await SingleSignOn.ResolveAccountAsync(users, providers, SingleSignOn.Google, External("rina@acme.co.id", "true"));
        Assert.Equal(account.Id, ok.Account?.Id, "Google: verified e-mail matches the account (case-insensitive)");
        Assert.Equal("unverified", (await SingleSignOn.ResolveAccountAsync(users, providers, SingleSignOn.Google, External("rina@acme.co.id", "false"))).Error, "Google: unverified refused");
        Assert.Equal("unverified", (await SingleSignOn.ResolveAccountAsync(users, providers, SingleSignOn.Google, External("rina@acme.co.id"))).Error, "Google: missing email_verified refused");
        Assert.Equal(account.Id, (await SingleSignOn.ResolveAccountAsync(users, providers, SingleSignOn.Microsoft, External("rina@acme.co.id"))).Account?.Id, "Entra (own directory): e-mail matches");
        Assert.Equal("domain", (await SingleSignOn.ResolveAccountAsync(users, providers, SingleSignOn.Google, External("rina@gmail.com", "true"))).Error, "other domains refused");
        Assert.Equal("noaccount", (await SingleSignOn.ResolveAccountAsync(users, providers, SingleSignOn.Google, External("budi@acme.com", "true"))).Error, "no account, no sign-in (never created)");
        Assert.Equal("noemail", (await SingleSignOn.ResolveAccountAsync(users, providers, SingleSignOn.Microsoft, External(null))).Error, "no e-mail refused");
        Assert.True(SingleSignOn.Describe("noaccount") is { Length: > 0 } && SingleSignOn.Describe(null) is null, "login page messages");
    }

    [Test]
    public Task Two_factor_qr_code_matches_reference_encoder()
    {
        // Reference: python-qrcode 8.2, byte mode, level M, mask 2, border 0 → rows of 0/1 joined by '\n', SHA-256.
        const string text = "otpauth://totp/FrogFrame:admin?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=FrogFrame&digits=6";
        var m = QrCode.Encode(text, forceMask: 2);
        var rows = Enumerable.Range(0, m.GetLength(0)).Select(y => string.Concat(Enumerable.Range(0, m.GetLength(1)).Select(x => m[y, x] ? '1' : '0')));
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(string.Join("\n", rows))));
        Assert.Equal(41, m.GetLength(0), "version 6 for this URI");
        Assert.Equal(QrReferenceHash, hash, "matrix identical to the reference encoder");
        Assert.True(QrCode.ToSvg(text).StartsWith("<svg") && TwoFactorSetup.FormatKey("JBSWY3DPEHPK3PXP") == "jbsw y3dp ehpk 3pxp", "SVG and key format");
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('x', 300)), "too long refused");
        return Task.CompletedTask;
    }

    private const string QrReferenceHash = "14DF8875207E19B400DFD4EDFF5E0D5B7A148B1A98038C3ADA99C329A06BDA66";

    [Test]
    public async Task Two_factor_fields_round_trip_in_postgres()
    {
        var pg = Environment.GetEnvironmentVariable("FROGFRAME_TEST_PG");
        if (string.IsNullOrEmpty(pg)) return; // runs with a PostgreSQL test database
        await using var sp = BuildIdentityProvider(s => s.AddFrogFrameworkPostgreSql(pg));
        await sp.MigrateFrogFrameworkDatabaseAsync();
        var (scope, _) = IdentityRequest(sp);
        await using var _s = scope;
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var repo = scope.ServiceProvider.GetRequiredService<IUserAccountRepository>();
        Assert.True(repo is PostgresUserAccountRepository, "PostgreSQL repository registered");

        var user = await CreateIdentityUserAsync(users, "tf-pg-" + Guid.NewGuid().ToString("N")[..8]);
        Assert.True(!(await repo.FindByIdAsync(user.Id))!.TwoFactorEnabled, "new accounts default to off");
        var (key, codes) = await EnrolAsync(users, user);

        var stored = (await repo.FindByIdAsync(user.Id))!;
        Assert.True(stored.TwoFactorEnabled && stored.AuthenticatorKey is { Length: > 0 } && stored.RecoveryCodes.Count == 10, "flag, secret and codes stored");
        var fresh = (await users.FindByIdAsync(user.Id))!;
        Assert.Equal(key, await users.GetAuthenticatorKeyAsync(fresh), "secret decrypts after reload");
        Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(fresh, codes[0])).Succeeded, "recovery code redeemed");
        Assert.Equal(9, (await repo.FindByIdAsync(user.Id))!.RecoveryCodes.Count, "redeemed code removed in the database");
        await repo.DeleteAsync(user.Id);
    }
}
