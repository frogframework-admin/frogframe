using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.RegularExpressions;
using Framework.Scripting.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Framework.Scripting;

public sealed class ScriptCompiler
{
    private static readonly List<MetadataReference> DefaultReferences = BuildDefaultReferences();

    private static List<MetadataReference> BuildDefaultReferences()
    {
        var assemblies = new HashSet<Assembly>
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(List<>).Assembly,
            typeof(Regex).Assembly,
            typeof(Task).Assembly,
            typeof(ScriptContext).Assembly
        };

        try { assemblies.Add(Assembly.Load("System.Runtime")); } catch { }
        try { assemblies.Add(Assembly.Load("netstandard")); } catch { }
        try { assemblies.Add(Assembly.Load("System.Collections")); } catch { }
        try { assemblies.Add(Assembly.Load("System.Linq")); } catch { }
        try { assemblies.Add(Assembly.Load("System.Threading.Tasks")); } catch { }

        return assemblies
            .Where(a => !string.IsNullOrEmpty(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
    }

    public static IReadOnlyList<string> Validate(string userCode)
    {
        var rawTree = CSharpSyntaxTree.ParseText(userCode);

        // Security AST analysis
        var securityViolations = ScriptSecurityAnalyzer.Analyze(rawTree);
        if (securityViolations.Count > 0)
        {
            return securityViolations;
        }

        // Full compilation check
        var safeId = "Validate";
        var wrapped = WrapUserCode(safeId, 1, userCode);
        var wrappedTree = CSharpSyntaxTree.ParseText(wrapped);

        var compilation = CSharpCompilation.Create(
            $"Script_Val_{Guid.NewGuid():N}",
            [wrappedTree],
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release)
        );

        var diagnostics = compilation.GetDiagnostics();
        var errors = diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d =>
            {
                var lineSpan = d.Location.GetLineSpan();
                // Map line back to approximate user code line
                var line = Math.Max(1, lineSpan.StartLinePosition.Line - 18);
                return $"Line {line}: {d.GetMessage()}";
            })
            .ToList();

        return errors;
    }

    public static CompiledScript Compile(string scriptId, int version, string userCode)
    {
        var rawTree = CSharpSyntaxTree.ParseText(userCode);

        // 1. Security AST Analysis
        var securityViolations = ScriptSecurityAnalyzer.Analyze(rawTree);
        if (securityViolations.Count > 0)
        {
            throw new ScriptSecurityException(string.Join("\n", securityViolations));
        }

        // 2. Budget Rewriter Injection
        var rewriter = new ScriptBudgetRewriter();
        var rewrittenRoot = rewriter.Visit(rawTree.GetRoot());
        var rewrittenCode = rewrittenRoot.ToFullString();

        // 3. Wrap into executable class
        var safeId = Regex.Replace(scriptId, "[^a-zA-Z0-9_]", "_");
        var className = $"ScriptRunner_{safeId}_v{version}";
        var wrappedSource = WrapUserCode(safeId, version, rewrittenCode);
        var syntaxTree = CSharpSyntaxTree.ParseText(wrappedSource);

        var assemblyName = $"FrogFrame_Script_{safeId}_v{version}_{Guid.NewGuid():N}";
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            DefaultReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release)
        );

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d =>
                {
                    var lineSpan = d.Location.GetLineSpan();
                    var line = Math.Max(1, lineSpan.StartLinePosition.Line - 18);
                    return $"Line {line}: {d.GetMessage()}";
                })
                .ToList();

            throw new ScriptCompilationException("Script compilation failed:\n" + string.Join("\n", errors), errors);
        }

        peStream.Position = 0;
        var alc = new AssemblyLoadContext($"ALC_{assemblyName}", isCollectible: true);
        var assembly = alc.LoadFromStream(peStream);
        var type = assembly.GetType($"FrogFrame.GeneratedScripts.{className}")
            ?? throw new InvalidOperationException($"Type FrogFrame.GeneratedScripts.{className} not found in emitted assembly.");

        var method = type.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("ExecuteAsync method not found in generated script runner.");

        var runner = (Func<ScriptContext, Task>)method.CreateDelegate(typeof(Func<ScriptContext, Task>));
        return new CompiledScript(scriptId, version, runner, alc);
    }

    private static string WrapUserCode(string safeId, int version, string bodyCode)
    {
        return $$"""
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Framework.Scripting.Abstractions;

namespace FrogFrame.GeneratedScripts;

public static class ScriptRunner_{{safeId}}_v{{version}}
{
    public static async Task ExecuteAsync(ScriptContext ctx)
    {
        var Doc = ctx.Doc;
        var OldDoc = ctx.OldDoc;
        var User = ctx.User;
        var Docs = ctx.Docs;
        var Log = ctx.Log;
        var Email = ctx.Email;
        var Values = ctx.Values;
        var __budget = ctx.__budget;
        var CancellationToken = ctx.CancellationToken;

        {{bodyCode}}

        ctx.Doc = Doc;
        ctx.OldDoc = OldDoc;
        await Task.CompletedTask;
    }
}
""";
    }
}
