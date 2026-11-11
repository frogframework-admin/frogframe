using Framework.Scripting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Framework.Web;

/// <summary>
/// Kill switch for server scripts, read from <c>Scripting:Enabled</c> on every check so a configuration reload
/// (e.g. editing appsettings.json) takes effect without a restart.
/// When the key is not set, scripts are enabled only in the Development environment: turning them on in
/// production is an explicit decision.
/// </summary>
public sealed class ConfigurationScriptingSwitch(IConfiguration? configuration, IHostEnvironment? environment) : IScriptingSwitch
{
    public const string ConfigKey = "Scripting:Enabled";

    public bool IsEnabled =>
        configuration?.GetValue<bool?>(ConfigKey) ?? (environment is null || environment.IsDevelopment());
}
