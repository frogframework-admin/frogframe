using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Framework.Scripting.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Framework.Scripting;

/// <summary>
/// Compiles server scripts in four steps:
/// 1. raw checks on the user code (no using directives, no preprocessor directives, no type declarations);
/// 2. structural check: the user code must form exactly one block, so it cannot close the generated method
///    and declare its own members (static constructors, finalizers, module initializers);
/// 3. semantic allowlist analysis of the user block (<see cref="ScriptSecurityAnalyzer"/>);
/// 4. budget instrumentation (<see cref="ScriptBudgetRewriter"/>), emit, and load into a collectible
///    <see cref="AssemblyLoadContext"/>.
/// </summary>
public sealed class ScriptCompiler
{
    private const string GeneratedNamespace = "FrogFrame.GeneratedScripts";
    private const string ScriptFileName = "script";

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default;

    private static readonly CSharpCompilationOptions CompilationOptions =
        new(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, allowUnsafe: false);

    private static readonly List<MetadataReference> DefaultReferences = BuildDefaultReferences();

    private static List<MetadataReference> BuildDefaultReferences()
    {
        // The core library is referenced so ordinary BCL types resolve. What scripts may actually use is decided by
        // the semantic allowlist in ScriptSecurityAnalyzer, not by the reference list.
        var assemblies = new HashSet<Assembly>
        {
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(List<>).Assembly,
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

    /// <summary>Validates a script for the editor. Returns an empty list when the script can be compiled and run.</summary>
    public static IReadOnlyList<string> Validate(string userCode) =>
        Prepare("Validate", 1, userCode).Errors;

    public static CompiledScript Compile(string scriptId, int version, string userCode)
    {
        var safeId = Regex.Replace(scriptId, "[^a-zA-Z0-9_]", "_");
        var prepared = Prepare(safeId, version, userCode);

        if (prepared.SecurityViolations.Count > 0)
            throw new ScriptSecurityException(string.Join("\n", prepared.SecurityViolations));
        if (prepared.Errors.Count > 0)
            throw new ScriptCompilationException("Script compilation failed:\n" + string.Join("\n", prepared.Errors), prepared.Errors);

        // Instrument with budget ticks, using the semantic model of the analyzed tree.
        var rewriter = new ScriptBudgetRewriter(prepared.Model!);
        var rewrittenRoot = rewriter.Visit(prepared.Tree!.GetRoot())!;
        var rewrittenTree = CSharpSyntaxTree.Create((CSharpSyntaxNode)rewrittenRoot, ParseOptions);

        var assemblyName = $"FrogFrame_Script_{safeId}_v{version}_{Guid.NewGuid():N}";
        var compilation = CSharpCompilation.Create(assemblyName, [rewrittenTree], DefaultReferences, CompilationOptions);

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        if (!emitResult.Success)
        {
            var errors = FormatDiagnostics(emitResult.Diagnostics);
            throw new ScriptCompilationException("Script compilation failed:\n" + string.Join("\n", errors), errors);
        }

        peStream.Position = 0;
        var className = ClassName(safeId, version);
        var alc = new AssemblyLoadContext($"ALC_{assemblyName}", isCollectible: true);
        var assembly = alc.LoadFromStream(peStream);
        var type = assembly.GetType($"{GeneratedNamespace}.{className}")
            ?? throw new InvalidOperationException($"Type {GeneratedNamespace}.{className} not found in emitted assembly.");

        var method = type.GetMethod("ExecuteAsync", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("ExecuteAsync method not found in generated script runner.");

        var runner = (Func<ScriptContext, Task>)method.CreateDelegate(typeof(Func<ScriptContext, Task>));
        return new CompiledScript(scriptId, version, runner, alc);
    }

    // ---------------------------------------------------------------- preparation

    private sealed record Prepared(
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> SecurityViolations,
        SyntaxTree? Tree,
        SemanticModel? Model);

    private static Prepared Prepare(string safeId, int version, string userCode)
    {
        userCode ??= "";

        // 1. Raw checks on the user code alone.
        var rawTree = CSharpSyntaxTree.ParseText(userCode, ParseOptions);
        var rawViolations = ScriptSecurityAnalyzer.AnalyzeRawCode(rawTree);
        if (rawViolations.Count > 0) return new Prepared(rawViolations, rawViolations, null, null);

        // 2. The user code must be exactly one block: an unbalanced '}' would close the generated method.
        var blockText = "{\n" + userCode + "\n}";
        // consumeFullText: false makes the parser stop after the first statement, so any text after the
        // closing brace (i.e. code that escaped the block) shows up as a shorter span.
        var asBlock = SyntaxFactory.ParseStatement(blockText, offset: 0, options: ParseOptions, consumeFullText: false);
        if (asBlock is not BlockSyntax || asBlock.FullSpan.Length != blockText.Length)
        {
            string[] structural = ["Line 1: The script contains unbalanced braces or code outside the script body."];
            return new Prepared(structural, structural, null, null);
        }

        // 3. Wrap, compile for analysis, and verify the generated structure is intact.
        var className = ClassName(safeId, version);
        var tree = CSharpSyntaxTree.ParseText(WrapUserCode(className, userCode), ParseOptions);
        var compilation = CSharpCompilation.Create($"Script_Analysis_{Guid.NewGuid():N}", [tree], DefaultReferences, CompilationOptions);
        var model = compilation.GetSemanticModel(tree);

        var userBlock = FindUserBlock(tree, className);
        if (userBlock is null)
        {
            string[] structural = ["Line 1: The script contains code outside the script body."];
            return new Prepared(structural, structural, null, null);
        }

        var violations = ScriptSecurityAnalyzer.AnalyzeUserBlock(model, userBlock);
        var compileErrors = FormatDiagnostics(compilation.GetDiagnostics());

        var errors = violations.Concat(compileErrors).ToList();
        return new Prepared(errors, violations, tree, model);
    }

    /// <summary>
    /// Returns the user block when the generated file still has exactly one type with exactly one method,
    /// and that method's body contains exactly one nested block (the user code).
    /// </summary>
    private static BlockSyntax? FindUserBlock(SyntaxTree tree, string className)
    {
        var root = (CompilationUnitSyntax)tree.GetRoot();
        if (root.Members.Count != 1 || root.Members[0] is not FileScopedNamespaceDeclarationSyntax ns) return null;
        if (ns.Members.Count != 1 || ns.Members[0] is not ClassDeclarationSyntax cls || cls.Identifier.ValueText != className) return null;
        if (cls.Members.Count != 1 || cls.Members[0] is not MethodDeclarationSyntax { Identifier.ValueText: "ExecuteAsync", Body: { } body }) return null;

        var blocks = body.Statements.OfType<BlockSyntax>().ToList();
        return blocks.Count == 1 ? blocks[0] : null;
    }

    private static string ClassName(string safeId, int version) => $"ScriptRunner_{safeId}_v{version}";

    private static List<string> FormatDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d =>
            {
                var span = d.Location.GetMappedLineSpan();
                var line = span.Path == ScriptFileName ? span.StartLinePosition.Line + 1 : 1;
                return $"Line {line}: {d.GetMessage()}";
            })
            .Distinct()
            .ToList();

    /// <summary>
    /// The user code is placed inside its own block, after a <c>#line 1</c> directive so diagnostics report the
    /// line number the user sees in the editor. Wrapper state lives in variables starting with "__", which user
    /// code may not reference.
    /// </summary>
    private static string WrapUserCode(string className, string bodyCode)
    {
        return $$"""
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Framework.Scripting.Abstractions;

namespace {{GeneratedNamespace}};

public static class {{className}}
{
    public static async Task ExecuteAsync(ScriptContext ctx)
    {
        var {{ScriptBudgetRewriter.BudgetVariable}} = ctx.__budget;
        var Doc = ctx.Doc;
        var OldDoc = ctx.OldDoc;
        var User = ctx.User;
        var Docs = ctx.Docs;
        var Log = ctx.Log;
        var Email = ctx.Email;
        var Values = ctx.Values;
        var CancellationToken = ctx.CancellationToken;
        var Form = ctx.Form;
        {
#line 1 "{{ScriptFileName}}"
{{bodyCode}}
#line default
        }
        await Task.CompletedTask;
    }
}
""";
    }
}
