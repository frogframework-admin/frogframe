using Framework.Jobs;

namespace Framework.Integrations;

/// <summary>
/// Hourly scheduled background job that executes due Auto Repeat rules for recurring documents.
/// </summary>
public sealed class AutoRepeatJob : IScheduledJob
{
    public string Name => "AutoRepeatJob";
    public string Schedule => "@hourly";

    public async Task ExecuteAsync(ScheduledJobContext context, CancellationToken ct)
    {
        var autoRepeatService = (AutoRepeatService)(context.Services.GetService(typeof(AutoRepeatService))
            ?? throw new InvalidOperationException("AutoRepeatService is not registered in service provider."));
        var today = DateOnly.FromDateTime(context.ScheduledTime.DateTime);

        context.Logger.LogJobStarting(Name, context.TenantId, context.ScheduledTime);
        try
        {
            var summaries = await autoRepeatService.ExecuteDueAsync(context.TenantId, today, ct);
            var totalGenerated = summaries.Sum(s => s.DocumentsGenerated);
            if (totalGenerated > 0)
            {
                context.Logger.LogJobCompleted($"{Name}: Generated {totalGenerated} document(s)", context.TenantId);
            }
            else
            {
                context.Logger.LogJobCompleted(Name, context.TenantId);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogJobFailed(ex, Name, context.TenantId);
            throw;
        }
    }
}
