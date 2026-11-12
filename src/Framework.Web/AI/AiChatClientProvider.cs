using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;

namespace Framework.Web.AI;

/// <summary>Supplies the <see cref="IChatClient"/> used by the AI designer. Tests replace it with a fake.</summary>
public interface IAiChatClientProvider
{
    /// <summary>Returns the chat client, or throws <see cref="AiUnavailableException"/> when AI is off or misconfigured.</summary>
    IChatClient GetChatClient();
}

public sealed class AiUnavailableException(string message) : Exception(message);

/// <summary>
/// Azure OpenAI in Microsoft Foundry through the OpenAI-compatible v1 endpoint
/// (<c>https://&lt;resource&gt;.openai.azure.com/openai/v1/</c>), using the official OpenAI .NET SDK and
/// Microsoft.Extensions.AI. Authentication: Managed Identity (production), DefaultAzureCredential (development) or API key.
/// The client is created on first use and rebuilt when the AI configuration changes.
/// </summary>
public sealed class AzureOpenAiChatClientProvider(IConfiguration? configuration, IAiSwitch aiSwitch, ILoggerFactory? loggerFactory = null)
    : IAiChatClientProvider
{
    private readonly object _gate = new();
    private (string Key, IChatClient Client)? _cached;

    public IChatClient GetChatClient()
    {
        if (aiSwitch.DisabledReason is { } reason) throw new AiUnavailableException(reason);

        var o = AiOptions.From(configuration);
        var key = $"{o.Endpoint}|{o.Deployment}|{o.Auth}|{o.ManagedIdentityClientId}|{o.TokenScope}|{o.MaxToolCalls}|{o.ApiKey?.GetHashCode()}";

        lock (_gate)
        {
            if (_cached is { } c && c.Key == key) return c.Client;
            var client = Create(o);
            _cached = (key, client);
            return client;
        }
    }

    private IChatClient Create(AiOptions o)
    {
        var clientOptions = new OpenAIClientOptions { Endpoint = new Uri(o.Endpoint!) };

#pragma warning disable OPENAI001
        OpenAIClient openAi = o.Auth.ToLowerInvariant() switch
        {
            "apikey" => new OpenAIClient(new ApiKeyCredential(o.ApiKey!), clientOptions),
            "defaultcredential" => new OpenAIClient(new BearerTokenPolicy(new DefaultAzureCredential(), o.TokenScope), clientOptions),
            "managedidentity" => new OpenAIClient(new BearerTokenPolicy(ManagedIdentity(o), o.TokenScope), clientOptions),
            _ => throw new AiUnavailableException($"AI:Auth '{o.Auth}' is not supported. Use ManagedIdentity, DefaultCredential or ApiKey."),
        };
#pragma warning restore OPENAI001

        // The agent reuses this FunctionInvokingChatClient instead of adding its own, so the tool-call limit applies.
        return openAi.GetChatClient(o.Deployment!)
            .AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation(loggerFactory, f =>
            {
                f.MaximumIterationsPerRequest = Math.Clamp(o.MaxToolCalls, 1, 20);
                f.AllowConcurrentInvocation = false;
                f.IncludeDetailedErrors = false;
            })
            .Build();
    }

    private static TokenCredential ManagedIdentity(AiOptions o) =>
        string.IsNullOrWhiteSpace(o.ManagedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(o.ManagedIdentityClientId));
}
