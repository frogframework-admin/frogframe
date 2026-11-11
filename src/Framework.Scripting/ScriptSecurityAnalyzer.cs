using Framework.Scripting.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Framework.Scripting;

public sealed class ScriptSecurityAnalyzer : CSharpSyntaxWalker
{
    private static readonly HashSet<string> BlockedNamespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.IO",
        "System.Reflection",
        "System.Diagnostics",
        "System.Threading",
        "System.Runtime.InteropServices",
        "System.Runtime.CompilerServices",
        "System.Net",
        "System.Net.Http",
        "System.Net.Sockets",
        "System.Security",
        "System.Management",
        "Microsoft.Win32",
        "Framework.Persistence",
        "Framework.Web"
    };

    private static readonly HashSet<string> BlockedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Process",
        "File",
        "FileInfo",
        "Directory",
        "DirectoryInfo",
        "FileStream",
        "StreamWriter",
        "StreamReader",
        "Path",
        "Environment",
        "AppDomain",
        "Assembly",
        "Type",
        "Activator",
        "Thread",
        "ThreadPool",
        "TaskFactory",
        "GC",
        "Marshal",
        "DllImport",
        "Socket",
        "HttpClient",
        "WebClient",
        "WebRequest",
        "HttpWebRequest"
    };

    private readonly List<string> _violations = [];

    public IReadOnlyList<string> Violations => _violations;

    public static IReadOnlyList<string> Analyze(SyntaxTree tree)
    {
        var walker = new ScriptSecurityAnalyzer();
        walker.Visit(tree.GetRoot());
        return walker.Violations;
    }

    public override void VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var name = node.Name?.ToString() ?? string.Empty;
        if (BlockedNamespaces.Any(ns => name.Equals(ns, StringComparison.OrdinalIgnoreCase) ||
                                        name.StartsWith(ns + ".", StringComparison.OrdinalIgnoreCase)))
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _violations.Add($"Line {line}: Using directive '{name}' is forbidden for security.");
        }
        base.VisitUsingDirective(node);
    }

    public override void VisitUnsafeStatement(UnsafeStatementSyntax node)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        _violations.Add($"Line {line}: 'unsafe' code blocks are strictly forbidden.");
        base.VisitUnsafeStatement(node);
    }

    public override void VisitFixedStatement(FixedStatementSyntax node)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        _violations.Add($"Line {line}: 'fixed' pointer blocks are strictly forbidden.");
        base.VisitFixedStatement(node);
    }

    public override void VisitStackAllocArrayCreationExpression(StackAllocArrayCreationExpressionSyntax node)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        _violations.Add($"Line {line}: 'stackalloc' memory allocation is forbidden.");
        base.VisitStackAllocArrayCreationExpression(node);
    }

    public override void VisitPointerType(PointerTypeSyntax node)
    {
        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        _violations.Add($"Line {line}: Pointer types are strictly forbidden.");
        base.VisitPointerType(node);
    }

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        var id = node.Identifier.Text;

        if (string.Equals(id, "dynamic", StringComparison.OrdinalIgnoreCase))
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _violations.Add($"Line {line}: The 'dynamic' keyword is forbidden in server scripts.");
        }

        if (BlockedTypes.Contains(id))
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _violations.Add($"Line {line}: Access to type '{id}' is forbidden for security.");
        }

        base.VisitIdentifierName(node);
    }

    public override void VisitAttribute(AttributeSyntax node)
    {
        var name = node.Name.ToString();
        if (name.Contains("DllImport") || BlockedTypes.Any(t => name.Contains(t)))
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _violations.Add($"Line {line}: Attribute '{name}' is forbidden for security.");
        }
        base.VisitAttribute(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var memberName = node.Name.Identifier.Text;
        if (node.Expression.ToString() == "Task" && memberName == "Run")
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _violations.Add($"Line {line}: 'Task.Run' background thread spawning is forbidden.");
        }

        base.VisitMemberAccessExpression(node);
    }
}
