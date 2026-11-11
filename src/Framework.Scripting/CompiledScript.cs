using System.Reflection;
using System.Runtime.Loader;
using Framework.Scripting.Abstractions;

namespace Framework.Scripting;

public sealed class CompiledScript : IDisposable
{
    private AssemblyLoadContext? _alc;
    private bool _disposed;

    public CompiledScript(string scriptId, int version, Func<ScriptContext, Task> runner, AssemblyLoadContext alc)
    {
        ScriptId = scriptId;
        Version = version;
        Runner = runner;
        _alc = alc;
    }

    public string ScriptId { get; }
    public int Version { get; }
    public Func<ScriptContext, Task> Runner { get; }

    public bool IsAlive => _alc is not null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_alc is not null)
        {
            try
            {
                _alc.Unload();
            }
            catch
            {
                // Unload attempt
            }
            _alc = null;
        }
    }
}
