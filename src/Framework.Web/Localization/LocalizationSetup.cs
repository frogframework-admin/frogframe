using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using BlazorBlueprint.Components;
using Framework.Preferences;
using Framework.Web.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Framework.Localization;

public static class LocalizationSetup
{
    /// <summary>User preference key holding the chosen UI language ("id"/"en").</summary>
    public const string PreferenceKey = "ui.language";

    /// <summary>Configuration key for the default language of users who have not chosen one.</summary>
    public const string DefaultLanguageConfigKey = "Localization:DefaultLanguage";

    /// <summary>Formats (numbers, dates) stay Indonesian in every language: inputs parse "1.000,5" and stored values are invariant.</summary>
    public const string FormatCulture = "id-ID";

    /// <summary>Dictionaries shipped with the framework (embedded resources FrogFrame.Translations.*.json).</summary>
    public static IEnumerable<TranslationFile> FrameworkTranslations()
    {
        var asm = typeof(LocalizationSetup).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("FrogFrame.Translations.", StringComparison.Ordinal)).Order())
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            yield return TranslationFile.Parse(reader.ReadToEnd(), name);
        }
    }

    /// <summary>Registers the catalog, the per-circuit <see cref="UiText"/> and a language-aware BlazorBlueprint localizer.</summary>
    public static IServiceCollection AddFrogFrameLocalization(this IServiceCollection services, IEnumerable<TranslationFile> applicationFiles)
    {
        var files = FrameworkTranslations().Concat(applicationFiles).ToList();
        services.TryAddSingleton<ITranslationStore, InMemoryTranslationStore>();
        services.AddSingleton(sp => new TranslationCatalog(files, sp.GetRequiredService<ITranslationStore>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<TranslationCatalog>>(), sp.GetService<TimeProvider>()));
        services.AddScoped<UiText>();

        // BlazorBlueprint registers one English localizer for everyone; replace it with one that follows the user's language.
        var english = services.LastOrDefault(d => d.ServiceType == typeof(IBbLocalizer))?.ImplementationInstance as IBbLocalizer;
        services.Replace(ServiceDescriptor.Scoped<IBbLocalizer>(sp => new FrogFrameBbLocalizer(english ?? new DefaultBbLocalizer(), sp.GetRequiredService<UiText>())));
        return services;
    }

    /// <summary>
    /// Request localization: the UI culture follows the user's language (cookie, then saved preference, then
    /// <c>Localization:DefaultLanguage</c>, default English); the format culture is always id-ID.
    /// Also maps <c>GET /ui/language/{code}?returnUrl=…</c>, used by the language switcher.
    /// Call after <c>UseAuthentication</c> so the saved preference of signed-in users can be read.
    /// </summary>
    public static WebApplication UseFrogFrameLocalization(this WebApplication app)
    {
        var configured = Languages.Normalize(app.Configuration[DefaultLanguageConfigKey]) ?? Languages.English;
        var uiCultures = Languages.Supported.Select(l => new CultureInfo(Languages.UiCultureName(l.Code))).ToList();
        var options = new RequestLocalizationOptions
        {
            DefaultRequestCulture = new RequestCulture(FormatCulture, Languages.UiCultureName(configured)),
            SupportedCultures = [new CultureInfo(FormatCulture)],
            SupportedUICultures = uiCultures,
            FallBackToParentUICultures = true,
        };
        options.RequestCultureProviders.Clear();
        options.RequestCultureProviders.Add(new CookieRequestCultureProvider());
        options.RequestCultureProviders.Add(new UserPreferenceCultureProvider());
        app.UseRequestLocalization(options);

        app.MapGet("/ui/language/{code}", async (HttpContext http, string code, string? returnUrl) =>
        {
            var language = Languages.Normalize(code);
            if (language is null) return Results.BadRequest("Unsupported language.");
            SetCookie(http, language);
            if (ClaimsUserMapper.ToUserContext(http.User) is { } user)
            {
                var prefs = http.RequestServices.GetService<IUserPreferenceStore>();
                if (prefs is not null) await prefs.SetAsync(user.TenantId, user.Id, PreferenceKey, language, http.RequestAborted);
            }
            return Results.LocalRedirect(IsLocal(returnUrl) ? returnUrl! : "/");
        });
        return app;
    }

    internal static void SetCookie(HttpContext http, string language) =>
        http.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(FormatCulture, Languages.UiCultureName(language))),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax });

    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));

    /// <summary>Language saved by a signed-in user on another device. Writes the cookie so later requests skip the lookup.</summary>
    private sealed class UserPreferenceCultureProvider : RequestCultureProvider
    {
        public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext http)
        {
            if (ClaimsUserMapper.ToUserContext(http.User) is not { } user) return null;
            var prefs = http.RequestServices.GetService<IUserPreferenceStore>();
            if (prefs is null) return null;
            string? language;
            try { language = Languages.Normalize(await prefs.GetAsync<string>(user.TenantId, user.Id, PreferenceKey, http.RequestAborted)); }
            catch (Exception) { return null; } // never fail a request because of the language
            if (language is null) return null;
            if (!http.Response.HasStarted) SetCookie(http, language);
            return new ProviderCultureResult(FormatCulture, Languages.UiCultureName(language));
        }
    }
}

/// <summary>BlazorBlueprint texts (pagination, date picker, data grid…) in the user's language.</summary>
public sealed class FrogFrameBbLocalizer(IBbLocalizer english, UiText text) : IBbLocalizer
{
    public string this[string key] => text[english[key]];

    public string this[string key, params object[] arguments]
    {
        get
        {
            var format = text[english[key]];
            try { return string.Format(CultureInfo.CurrentCulture, format, arguments); }
            catch (FormatException) { return english[key, arguments]; }
        }
    }
}
