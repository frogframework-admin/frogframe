using Framework.Hooks;

namespace Framework.Accounting;

public static class LedgerHookExtensions
{
    public static void AddLedgers(this HookContext context, IEnumerable<LedgerLine> lines) =>
        context.Extensions.Add(new LedgerPosting(lines.ToList()));

    public static void AddLedger(this HookContext context, LedgerLine line) =>
        context.Extensions.Add(new LedgerPosting([line]));
}
