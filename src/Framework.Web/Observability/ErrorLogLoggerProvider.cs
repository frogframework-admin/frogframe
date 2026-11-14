using Framework.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Framework.Web.Observability;

public sealed class ErrorLogLoggerProvider(IServiceProvider services) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ErrorLogLogger(categoryName, services);

    public void Dispose() { }
}

internal sealed class ErrorLogLogger(string categoryName, IServiceProvider services) : ILogger
{
    private static readonly AsyncLocal<bool> InLog = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (logLevel < LogLevel.Error || exception is null) return;
        if (InLog.Value) return;

        // Skip internal/persistence logging to prevent infinite loops
        if (categoryName.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase) ||
            categoryName.StartsWith("Framework.Persistence", StringComparison.OrdinalIgnoreCase) ||
            categoryName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            categoryName.StartsWith("Framework.Web.Components.FrogFrameErrorBoundary", StringComparison.OrdinalIgnoreCase) ||
            categoryName.StartsWith("FrogFrame.Http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            InLog.Value = true;
            var store = services.GetService<IErrorLogStore>();
            if (store is null) return;

            var msg = exception.Message;
            if (string.IsNullOrWhiteSpace(msg) && formatter is not null)
            {
                msg = formatter(state, exception);
            }

            var entry = new ErrorLogEntry
            {
                TenantId = "system",
                UserId = "system",
                Method = "SYSTEM",
                Path = categoryName,
                ErrorType = exception.GetType().FullName ?? exception.GetType().Name,
                Message = string.IsNullOrWhiteSpace(msg) ? exception.GetType().Name : msg,
                StackTrace = exception.StackTrace,
                Timestamp = DateTimeOffset.UtcNow,
                Seen = false
            };

            // Non-blocking log to store
            _ = Task.Run(async () =>
            {
                try
                {
                    await store.LogAsync(entry);
                }
                catch
                {
                    // Ignore logger store failures
                }
            });
        }
        catch
        {
            // Safeguard: never crash from logger
        }
        finally
        {
            InLog.Value = false;
        }
    }
}
