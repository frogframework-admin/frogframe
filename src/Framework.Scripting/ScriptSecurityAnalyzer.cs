using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Framework.Scripting;

/// <summary>
/// Security analyzer for server scripts. Deny by default: every symbol and every expression type used by the
/// script must resolve (through the semantic model, not by name) to an explicitly allowed type and member.
///
/// Why semantic: a name-based denylist is bypassed by unicode escapes (<c>File</c>), verbatim identifiers
/// (<c>@File</c>), reflection through members that are not on the list (<c>typeof(object).Module.GetType(...)</c>)
/// or types that were simply forgotten (<c>Task.Factory</c>, <c>Timer</c>, <c>Parallel</c>).
///
/// Only the user block of the generated wrapper is analyzed; the wrapper itself is trusted framework code.
/// </summary>
public sealed class ScriptSecurityAnalyzer : CSharpSyntaxWalker
{
    /// <summary>Types allowed with all their members (metadata names of the generic definitions).</summary>
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.Ordinal)
    {
        // System
        "System.Math", "System.DateTimeOffset", "System.DateOnly", "System.TimeOnly", "System.TimeSpan", "System.Guid",
        "System.StringComparison", "System.StringComparer", "System.StringSplitOptions", "System.MidpointRounding",
        "System.DayOfWeek", "System.DateTimeKind", "System.Convert", "System.Index", "System.Range", "System.Random",
        "System.IComparable", "System.IComparable`1", "System.IEquatable`1", "System.IFormattable", "System.IFormatProvider",
        "System.Exception", "System.ArgumentException", "System.ArgumentNullException", "System.ArgumentOutOfRangeException",
        "System.InvalidOperationException", "System.NotSupportedException", "System.FormatException",
        "System.OverflowException", "System.DivideByZeroException",
        "System.ValueTuple", "System.ValueTuple`1", "System.ValueTuple`2", "System.ValueTuple`3", "System.ValueTuple`4",
        "System.ValueTuple`5", "System.ValueTuple`6", "System.ValueTuple`7", "System.ValueTuple`8",
        "System.Action", "System.Action`1", "System.Action`2", "System.Action`3", "System.Action`4",
        "System.Func`1", "System.Func`2", "System.Func`3", "System.Func`4", "System.Func`5",
        "System.Predicate`1", "System.Comparison`1", "System.Converter`2",

        // System.Collections.Generic
        "System.Collections.Generic.List`1", "System.Collections.Generic.Dictionary`2", "System.Collections.Generic.HashSet`1",
        "System.Collections.Generic.SortedDictionary`2", "System.Collections.Generic.SortedList`2",
        "System.Collections.Generic.SortedSet`1", "System.Collections.Generic.Queue`1", "System.Collections.Generic.Stack`1",
        "System.Collections.Generic.KeyValuePair`2", "System.Collections.Generic.KeyValuePair",
        "System.Collections.Generic.IEnumerator`1", "System.Collections.Generic.IDictionary`2",
        "System.Collections.Generic.IReadOnlyDictionary`2", "System.Collections.Generic.ISet`1",
        "System.Collections.Generic.IReadOnlySet`1", "System.Collections.Generic.IComparer`1",
        "System.Collections.Generic.IEqualityComparer`1", "System.Collections.Generic.Comparer`1",
        "System.Collections.Generic.EqualityComparer`1", "System.Collections.Generic.KeyNotFoundException",
        "System.Collections.Generic.CollectionExtensions",
        "System.Collections.IEnumerator",

        // System.Linq (method syntax only; query syntax is rejected)
        "System.Linq.Enumerable", "System.Linq.IOrderedEnumerable`1", "System.Linq.IGrouping`2", "System.Linq.ILookup`2",

        // Text and formatting
        "System.Text.StringBuilder",
        "System.Globalization.NumberStyles", "System.Globalization.DateTimeStyles",

        // Tasks (members restricted below)
        "System.Threading.Tasks.Task", "System.Threading.Tasks.Task`1",
        "System.Threading.Tasks.ValueTask", "System.Threading.Tasks.ValueTask`1",
        "System.Threading.CancellationToken",
        "System.Globalization.CultureInfo",
    };

    /// <summary>Allowed special types (primitives, string, object, nullable, core collection interfaces).</summary>
    private static readonly HashSet<SpecialType> AllowedSpecialTypes =
    [
        SpecialType.System_Object, SpecialType.System_Boolean, SpecialType.System_Char, SpecialType.System_SByte,
        SpecialType.System_Byte, SpecialType.System_Int16, SpecialType.System_UInt16, SpecialType.System_Int32,
        SpecialType.System_UInt32, SpecialType.System_Int64, SpecialType.System_UInt64, SpecialType.System_Decimal,
        SpecialType.System_Single, SpecialType.System_Double, SpecialType.System_String, SpecialType.System_Void,
        SpecialType.System_Nullable_T, SpecialType.System_DateTime, SpecialType.System_Enum, SpecialType.System_ValueType,
        SpecialType.System_Array, SpecialType.System_IDisposable, SpecialType.System_Collections_IEnumerable,
        SpecialType.System_Collections_Generic_IEnumerable_T, SpecialType.System_Collections_Generic_IList_T,
        SpecialType.System_Collections_Generic_ICollection_T, SpecialType.System_Collections_Generic_IEnumerator_T,
        SpecialType.System_Collections_Generic_IReadOnlyList_T, SpecialType.System_Collections_Generic_IReadOnlyCollection_T,
        SpecialType.System_Collections_IEnumerator,
    ];

    /// <summary>For these types only the listed members may be used.</summary>
    private static readonly Dictionary<string, HashSet<string>> RestrictedMembers = new(StringComparer.Ordinal)
    {
        // No Task.Run, Task.Factory, ContinueWith, Start, Wait, Result: no background work that outlives the budget.
        ["System.Threading.Tasks.Task"] = ["CompletedTask", "FromResult", "WhenAll", "WhenAny", "Delay",
            "IsCompleted", "IsCompletedSuccessfully", "IsFaulted", "IsCanceled"],
        ["System.Threading.Tasks.Task`1"] = ["IsCompleted", "IsCompletedSuccessfully", "IsFaulted", "IsCanceled"],
        ["System.Threading.Tasks.ValueTask"] = ["CompletedTask", "FromResult", "IsCompleted", "IsCompletedSuccessfully"],
        ["System.Threading.Tasks.ValueTask`1"] = ["IsCompleted", "IsCompletedSuccessfully"],
        // No Register(): callbacks would run outside the budget.
        ["System.Threading.CancellationToken"] = ["IsCancellationRequested", "ThrowIfCancellationRequested", "CanBeCanceled", "None"],
        // No CurrentCulture / DefaultThreadCurrentCulture setters: they would change the culture of the host thread.
        ["System.Globalization.CultureInfo"] = ["InvariantCulture", "GetCultureInfo", "Name"],
    };

    /// <summary>Members denied on every type (reflection entry points on System.Object).</summary>
    private static readonly HashSet<string> DeniedMembers = new(StringComparer.Ordinal)
    {
        "GetType", "MemberwiseClone", "Finalize",
    };

    private const string AbstractionsNamespace = "Framework.Scripting.Abstractions";
    private static readonly HashSet<string> DeniedAbstractionTypes = new(StringComparer.Ordinal) { "ScriptBudget" };

    private readonly SemanticModel _model;
    private readonly IAssemblySymbol _scriptAssembly;
    private readonly List<string> _violations = [];
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private ScriptSecurityAnalyzer(SemanticModel model) : base(SyntaxWalkerDepth.Node)
    {
        _model = model;
        _scriptAssembly = model.Compilation.Assembly;
    }

    /// <summary>
    /// Syntax checks on the raw user code, before it is wrapped: directives and using directives are not allowed.
    /// </summary>
    public static IReadOnlyList<string> AnalyzeRawCode(SyntaxTree rawTree)
    {
        var violations = new List<string>();
        var root = (CompilationUnitSyntax)rawTree.GetRoot();

        foreach (var u in root.Usings)
            violations.Add($"Line {Line(u)}: Using directive '{u.Name}' is not allowed. The available namespaces are imported automatically.");

        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true).Where(t => t.IsDirective))
            violations.Add($"Line {trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: Preprocessor directives are not allowed.");

        foreach (var member in root.Members.Where(m => m is not GlobalStatementSyntax))
            violations.Add($"Line {Line(member)}: Type and member declarations are not allowed in server scripts.");

        return violations;
    }

    /// <summary>Semantic checks on the user block inside the wrapped, compiled tree.</summary>
    public static IReadOnlyList<string> AnalyzeUserBlock(SemanticModel model, BlockSyntax userBlock)
    {
        var walker = new ScriptSecurityAnalyzer(model);
        walker.CheckReservedIdentifiers(userBlock);
        walker.Visit(userBlock);
        return walker._violations;
    }

    // ---------------------------------------------------------------- syntax rules

    public override void Visit(SyntaxNode? node)
    {
        if (node is null) return;

        switch (node.Kind())
        {
            case SyntaxKind.UnsafeStatement:
                Report(node, "'unsafe' code blocks are not allowed.");
                return;
            case SyntaxKind.FixedStatement:
                Report(node, "'fixed' statements are not allowed.");
                return;
            case SyntaxKind.StackAllocArrayCreationExpression:
            case SyntaxKind.ImplicitStackAllocArrayCreationExpression:
                Report(node, "'stackalloc' is not allowed.");
                return;
            case SyntaxKind.PointerType:
            case SyntaxKind.FunctionPointerType:
            case SyntaxKind.AddressOfExpression:
            case SyntaxKind.PointerIndirectionExpression:
            case SyntaxKind.PointerMemberAccessExpression:
            case SyntaxKind.SizeOfExpression:
                Report(node, "Pointer operations are not allowed.");
                return;
            case SyntaxKind.LocalFunctionStatement:
                Report(node, "Local functions are not allowed; use a lambda instead.");
                return;
            case SyntaxKind.GotoStatement:
            case SyntaxKind.GotoCaseStatement:
            case SyntaxKind.GotoDefaultStatement:
            case SyntaxKind.LabeledStatement:
                Report(node, "'goto' and labels are not allowed.");
                return;
            case SyntaxKind.LockStatement:
                Report(node, "'lock' is not allowed.");
                return;
            case SyntaxKind.AttributeList:
                Report(node, "Attributes are not allowed.");
                return;
            case SyntaxKind.ArgListExpression:
            case SyntaxKind.MakeRefExpression:
            case SyntaxKind.RefTypeExpression:
            case SyntaxKind.RefValueExpression:
                Report(node, "Undocumented keywords (__arglist, __makeref, __reftype, __refvalue) are not allowed.");
                return;
            case SyntaxKind.QueryExpression:
                Report(node, "LINQ query syntax is not allowed; use method syntax (e.g. .Where(x => ...)).");
                return;
        }

        if (node is ExpressionSyntax expression)
            CheckExpression(expression);

        if (node is VariableDeclaratorSyntax or SingleVariableDesignationSyntax or ForEachStatementSyntax or CatchDeclarationSyntax)
        {
            if (_model.GetDeclaredSymbol(node) is ILocalSymbol local)
                CheckType(node, local.Type);
        }

        base.Visit(node);
    }

    private void CheckReservedIdentifiers(BlockSyntax userBlock)
    {
        // The wrapper keeps its own state in variables starting with "__" (e.g. the execution budget).
        foreach (var token in userBlock.DescendantTokens().Where(t => t.IsKind(SyntaxKind.IdentifierToken)))
        {
            if (token.ValueText.StartsWith("__", StringComparison.Ordinal))
                Report(token.GetLocation(), $"Identifiers starting with '__' are reserved ('{token.ValueText}').");
        }
    }

    // ---------------------------------------------------------------- semantic rules

    private void CheckExpression(ExpressionSyntax expression)
    {
        var symbolInfo = _model.GetSymbolInfo(expression);
        if (symbolInfo.Symbol is { } symbol)
            CheckSymbol(expression, symbol);
        else
            foreach (var candidate in symbolInfo.CandidateSymbols)
                CheckSymbol(expression, candidate);

        var typeInfo = _model.GetTypeInfo(expression);
        CheckType(expression, typeInfo.Type);
        CheckType(expression, typeInfo.ConvertedType);
    }

    private void CheckSymbol(SyntaxNode node, ISymbol symbol)
    {
        switch (symbol)
        {
            case INamespaceSymbol:
                return; // namespaces are inert; the types reached through them are checked
            case ILocalSymbol local:
                CheckType(node, local.Type);
                return;
            case IParameterSymbol parameter:
                CheckType(node, parameter.Type);
                return;
            case IDiscardSymbol discard:
                CheckType(node, discard.Type);
                return;
            case ITypeSymbol type:
                CheckType(node, type);
                return;
            case IEventSymbol ev:
                Report(node, $"Events are not allowed ('{ev.ContainingType?.ToDisplayString()}.{ev.Name}').");
                return;
            case IMethodSymbol method:
                CheckMethod(node, method);
                return;
            case IPropertySymbol property:
                CheckMember(node, property.ContainingType, property.Name);
                CheckType(node, property.Type);
                return;
            case IFieldSymbol field:
                if (field.ContainingType is { IsTupleType: true }) { CheckType(node, field.Type); return; }
                CheckMember(node, field.ContainingType, field.Name);
                CheckType(node, field.Type);
                return;
            case IRangeVariableSymbol:
            case ILabelSymbol:
                return; // the syntax that creates them is already rejected
            default:
                Report(node, $"'{symbol.ToDisplayString()}' ({symbol.Kind}) is not allowed in server scripts.");
                return;
        }
    }

    private void CheckMethod(SyntaxNode node, IMethodSymbol method)
    {
        switch (method.MethodKind)
        {
            case MethodKind.AnonymousFunction:
                return; // the lambda itself; its body is visited separately
            case MethodKind.LocalFunction:
                Report(node, "Local functions are not allowed; use a lambda instead.");
                return;
            case MethodKind.FunctionPointerSignature:
                Report(node, "Function pointers are not allowed.");
                return;
        }

        if (SymbolEqualityComparer.Default.Equals(method.ContainingAssembly, _scriptAssembly))
            return; // the generated wrapper

        var definition = (method.ReducedFrom ?? method).OriginalDefinition;
        var memberName = method.MethodKind == MethodKind.Constructor ? ".ctor" : definition.Name;
        CheckMember(node, definition.ContainingType, memberName);

        CheckType(node, method.ReturnType);
        foreach (var typeArgument in method.TypeArguments)
            CheckType(node, typeArgument);
    }

    private void CheckMember(SyntaxNode node, INamedTypeSymbol? containingType, string memberName)
    {
        if (containingType is null) return;
        if (containingType.IsAnonymousType) return;
        if (SymbolEqualityComparer.Default.Equals(containingType.ContainingAssembly, _scriptAssembly)) return;

        var definition = containingType.OriginalDefinition;
        var typeName = FullMetadataName(definition);

        if (DeniedMembers.Contains(memberName) || memberName.StartsWith("__", StringComparison.Ordinal))
        {
            Report(node, $"'{definition.ToDisplayString()}.{memberName}' is not allowed in server scripts.");
            return;
        }

        if (!IsAllowedType(definition))
        {
            Report(node, $"'{definition.ToDisplayString()}.{memberName}' is not allowed in server scripts.");
            return;
        }

        if (RestrictedMembers.TryGetValue(typeName, out var allowed) && memberName != ".ctor" && !allowed.Contains(memberName))
            Report(node, $"'{definition.ToDisplayString()}.{memberName}' is not allowed in server scripts.");
    }

    private void CheckType(SyntaxNode node, ITypeSymbol? type)
    {
        switch (type)
        {
            case null:
                return;
            case { TypeKind: TypeKind.Dynamic }:
                Report(node, "The 'dynamic' type is not allowed in server scripts.");
                return;
            case IPointerTypeSymbol or IFunctionPointerTypeSymbol:
                Report(node, "Pointer types are not allowed in server scripts.");
                return;
            case IArrayTypeSymbol array:
                CheckType(node, array.ElementType);
                return;
            case ITypeParameterSymbol:
                return;
            case IErrorTypeSymbol:
                return; // reported as a compile error
            case INamedTypeSymbol named:
                if (named.IsAnonymousType) return;
                if (SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, _scriptAssembly)) return;
                if (named.IsTupleType)
                {
                    foreach (var element in named.TupleElements) CheckType(node, element.Type);
                    return;
                }
                if (!IsAllowedType(named.OriginalDefinition))
                {
                    Report(node, $"Type '{named.OriginalDefinition.ToDisplayString()}' is not allowed in server scripts.");
                    return;
                }
                foreach (var argument in named.TypeArguments) CheckType(node, argument);
                return;
            default:
                Report(node, $"Type '{type.ToDisplayString()}' is not allowed in server scripts.");
                return;
        }
    }

    private static bool IsAllowedType(INamedTypeSymbol definition)
    {
        if (AllowedSpecialTypes.Contains(definition.SpecialType)) return true;

        // Nested types (List<T>.Enumerator, Dictionary<K,V>.KeyCollection, StringBuilder handlers) follow their parent.
        if (definition.ContainingType is { } parent) return IsAllowedType(parent.OriginalDefinition);

        var ns = definition.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == AbstractionsNamespace) return !DeniedAbstractionTypes.Contains(definition.Name);

        return AllowedTypes.Contains(FullMetadataName(definition));
    }

    private static string FullMetadataName(INamedTypeSymbol type)
    {
        if (type.ContainingType is { } parent) return FullMetadataName(parent) + "+" + type.MetadataName;
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false } n ? n.ToDisplayString() + "." : "";
        return ns + type.MetadataName;
    }

    // ---------------------------------------------------------------- reporting

    private void Report(SyntaxNode node, string message) => Report(node.GetLocation(), message);

    private void Report(Location location, string message)
    {
        var line = location.GetMappedLineSpan().StartLinePosition.Line + 1;
        var text = $"Line {line}: {message}";
        if (_reported.Add(text)) _violations.Add(text);
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
