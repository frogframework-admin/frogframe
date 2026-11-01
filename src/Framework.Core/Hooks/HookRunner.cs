using Framework.Documents;

namespace Framework.Hooks;

public sealed class HookRunner(IEnumerable<IDocHookRegistration> registrations, IDocHookLogger log)
{
    public const int MaxDepth = 5;

    private sealed record CallFrame(string DocType, string Name, CallFrame? Parent);
    private readonly AsyncLocal<int> _depth = new();
    private readonly AsyncLocal<CallFrame?> _current = new();

    public IDisposable EnterDocument(string docType, string name)
    {
        var current = _current.Value;
        for (var frame = current; frame is not null; frame = frame.Parent)
            if (string.Equals(frame.DocType, docType, StringComparison.Ordinal)
                && string.Equals(frame.Name, name, StringComparison.Ordinal))
                throw new HookRecursionException($"Siklus hook terdeteksi untuk dokumen {docType} '{name}'.");

        if (_depth.Value >= MaxDepth)
            throw new HookRecursionException($"Kedalaman panggilan hook melebihi batas maksimum {MaxDepth}.");

        var priorDepth = _depth.Value;
        _depth.Value = priorDepth + 1;
        _current.Value = new CallFrame(docType, name, current);
        return new Scope(this, priorDepth, current);
    }

    public async Task RunAsync(DocHookStage stage, HookContext context, CancellationToken ct, IReadOnlyList<FieldChange>? changes = null)
    {
        foreach (var registration in registrations
                     .Where(r => string.Equals(r.DocType, context.Meta.Name, StringComparison.Ordinal) && r.Supports(stage))
                     .OrderBy(r => r.Order))
        {
            try
            {
                await registration.InvokeAsync(stage, context, changes, ct);
            }
            catch (Exception ex) when (ex is not ValidationFailedException and not OperationCanceledException and not DocumentHookException and not HookRecursionException)
            {
                log.LogHookFailure(ex, registration.HookType, context.Meta.Name, stage);
                throw new DocumentHookException(ex);
            }
        }
    }

    private sealed class Scope(HookRunner runner, int priorDepth, CallFrame? priorFrame) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            runner._depth.Value = priorDepth;
            runner._current.Value = priorFrame;
        }
    }
}