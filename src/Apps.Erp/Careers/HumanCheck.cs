using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Apps.Erp.Careers;

/// <summary>Checks that a public form was sent by a person (captcha). Not configured: every request passes.</summary>
public interface ICareersHumanCheck
{
    bool Enabled { get; }
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
}

/// <summary>
/// Captcha check with the "siteverify" protocol shared by Cloudflare Turnstile, hCaptcha and Google reCAPTCHA:
/// POST secret + response (+ remoteip) as a form, JSON reply with <c>success</c>. Settings:
/// <c>Careers:Captcha:VerifyUrl</c> (for example https://challenges.cloudflare.com/turnstile/v0/siteverify) and
/// <c>Careers:Captcha:Secret</c>. Both empty: disabled. A failed call to the provider rejects the request (fail closed).
/// </summary>
public sealed class SiteVerifyHumanCheck(IConfiguration config, IHttpClientFactory httpFactory, ILogger<SiteVerifyHumanCheck> log) : ICareersHumanCheck
{
    public const string HttpClientName = "erp-careers-captcha";

    private string? VerifyUrl => config["Careers:Captcha:VerifyUrl"];
    private string? Secret => config["Careers:Captcha:Secret"];

    public bool Enabled => !string.IsNullOrWhiteSpace(VerifyUrl) && !string.IsNullOrWhiteSpace(Secret);

    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        if (!Enabled) return true;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return false;
        try
        {
            var form = new Dictionary<string, string> { ["secret"] = Secret!, ["response"] = token };
            if (!string.IsNullOrWhiteSpace(remoteIp)) form["remoteip"] = remoteIp;
            using var response = await httpFactory.CreateClient(HttpClientName).PostAsync(VerifyUrl, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning("Captcha verification returned HTTP {Status}", (int)response.StatusCode);
                return false;
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            return json.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            log.LogWarning(ex, "Captcha verification failed; the request is rejected");
            return false;
        }
    }
}
