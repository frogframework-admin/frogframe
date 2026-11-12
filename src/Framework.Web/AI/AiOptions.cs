using Microsoft.Extensions.Configuration;

namespace Framework.Web.AI;

/// <summary>
/// Configuration section <c>AI</c>. The AI designer is off unless <see cref="Enabled"/> is true and an endpoint and
/// deployment are set.
/// </summary>
/// <example>
/// "AI": {
///   "Enabled": true,
///   "Provider": "AzureOpenAI",
///   "Endpoint": "https://my-resource.openai.azure.com/openai/v1/",
///   "Deployment": "gpt-5-mini",
///   "Auth": "ManagedIdentity"
/// }
/// </example>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    public bool Enabled { get; set; }

    /// <summary>Only "AzureOpenAI" is implemented (Azure OpenAI in Microsoft Foundry, OpenAI-compatible v1 endpoint).</summary>
    public string Provider { get; set; } = "AzureOpenAI";

    /// <summary>Azure OpenAI v1 endpoint, e.g. <c>https://&lt;resource&gt;.openai.azure.com/openai/v1/</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Deployment name created in Microsoft Foundry (not necessarily the model name).</summary>
    public string? Deployment { get; set; }

    /// <summary>
    /// "ManagedIdentity" (production; optional <see cref="ManagedIdentityClientId"/> for a user-assigned identity),
    /// "DefaultCredential" (development: Azure CLI / Visual Studio sign-in), or "ApiKey".
    /// </summary>
    public string Auth { get; set; } = "ManagedIdentity";

    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Only for Auth = "ApiKey". Keep it in a secret store or environment variable (AI__ApiKey), not in appsettings.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Entra ID token scope for the Azure OpenAI v1 endpoint.</summary>
    public string TokenScope { get; set; } = "https://ai.azure.com/.default";

    /// <summary>Maximum tool calls (list/validate) the model may make in one attempt.</summary>
    public int MaxToolCalls { get; set; } = 8;

    /// <summary>Attempts including repairs: the server re-validates every answer and sends errors back.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Overall time limit for one request (all attempts).</summary>
    public int TimeoutSeconds { get; set; } = 90;

    /// <summary>Maximum length of the user's description.</summary>
    public int MaxPromptChars { get; set; } = 4000;

    /// <summary>Requests per user per hour.</summary>
    public int RequestsPerUserPerHour { get; set; } = 30;

    public static AiOptions From(IConfiguration? configuration)
    {
        var options = new AiOptions();
        configuration?.GetSection(SectionName).Bind(options);
        return options;
    }
}

/// <summary>Whether the AI designer may be used right now. Read on every request so configuration reloads apply.</summary>
public interface IAiSwitch
{
    bool IsEnabled { get; }

    /// <summary>Why it is off (shown to System Managers), or null when enabled.</summary>
    string? DisabledReason { get; }
}

public sealed class ConfigurationAiSwitch(IConfiguration? configuration) : IAiSwitch
{
    public bool IsEnabled => DisabledReason is null;

    public string? DisabledReason
    {
        get
        {
            var o = AiOptions.From(configuration);
            if (!o.Enabled) return "AI is switched off (AI:Enabled = false).";
            if (!string.Equals(o.Provider, "AzureOpenAI", StringComparison.OrdinalIgnoreCase)) return $"AI provider '{o.Provider}' is not supported.";
            if (string.IsNullOrWhiteSpace(o.Endpoint) || string.IsNullOrWhiteSpace(o.Deployment)) return "AI:Endpoint and AI:Deployment must be set.";
            if (string.Equals(o.Auth, "ApiKey", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(o.ApiKey)) return "AI:ApiKey must be set when AI:Auth = ApiKey.";
            return null;
        }
    }
}
