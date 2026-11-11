using Framework.Hooks;

namespace Framework.Scripting;

public sealed class ServerScriptHookRunner(ServerScriptExecutionService service) : IServerScriptHookRunner
{
    public Task RunAsync(DocHookStage stage, HookContext context, CancellationToken ct) =>
        service.RunDocEventAsync(stage, context, ct);
}
