using Framework.Jobs;

namespace Framework.Scripting;

public sealed class ServerScriptScheduledJob : IScheduledJob
{
    public string Name => "ServerScriptScheduledJob";
    public string Schedule => "* * * * *";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var store = (IServerScriptStore)(context.Services.GetService(typeof(IServerScriptStore))
            ?? throw new InvalidOperationException("IServerScriptStore is not registered."));
        var executionService = (ServerScriptExecutionService)(context.Services.GetService(typeof(ServerScriptExecutionService))
            ?? throw new InvalidOperationException("ServerScriptExecutionService is not registered."));

        var scripts = await store.ListScheduledScriptsAsync(context.TenantId, ct);
        if (scripts.Count == 0) return;

        foreach (var script in scripts)
        {
            if (string.IsNullOrWhiteSpace(script.CronExpr)) continue;

            if (CronSchedule.IsDue(script.CronExpr, context.ScheduledTime))
            {
                try
                {
                    await executionService.ExecuteScheduledAsync(context.TenantId, script.Id, ct);
                }
                catch (Exception ex)
                {
                    context.Logger.LogJobFailed(ex, $"{Name}:{script.Name}", context.TenantId);
                }
            }
        }
    }
}
