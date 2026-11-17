using Framework.Documents;
using Framework.Notifications;
using Framework.Web.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;

namespace Framework.Web.Notifications;

/// <summary>
/// SignalR hub for real-time user notifications (including <c>@mention</c> alerts).
/// Connections join a tenant+user group derived strictly from the authenticated user claims.
/// </summary>
[Authorize]
public sealed class NotificationHub : Hub
{
    public const string Route = "/hubs/notifications";
    public const string ReceiveMethod = "ReceiveNotification";

    public static string GroupName(string tenantId, string userId) =>
        $"user:{tenantId.Trim()}:{userId.Trim().ToLowerInvariant()}";

    public override async Task OnConnectedAsync()
    {
        var user = ClaimsUserMapper.ToUserContext(Context.User);
        if (user is null)
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(user.TenantId, user.Id));
        await base.OnConnectedAsync();
    }
}

/// <summary>Forwards <see cref="INotificationLiveFeed"/> events to connected SignalR hub clients.</summary>
public sealed class SignalRNotificationBridge(
    INotificationLiveFeed liveFeed,
    IHubContext<NotificationHub> hubContext) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = liveFeed.SubscribeAll(n =>
            hubContext.Clients.Group(NotificationHub.GroupName(n.TenantId, n.UserId))
                .SendAsync(NotificationHub.ReceiveMethod, n));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
