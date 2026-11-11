using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Framework.Scripting;

/// <summary>
/// Injects <c>__budget.Tick();</c> at the start of every loop body, every lambda / anonymous method body and every
/// catch block of the user code. Ticks in lambdas cover LINQ callbacks and recursion through delegates; ticks in
/// catch blocks make a tripped budget impossible to swallow with try/catch.
///
/// The rewriter runs on the original wrapped tree and uses its semantic model to decide whether an expression-bodied
/// lambda returns a value (<c>x => x * 2</c> becomes <c>x => { Tick(); return x * 2; }</c>) or not
/// (<c>x => list.Add(x)</c> becomes <c>x => { Tick(); list.Add(x); }</c>).
/// </summary>
public sealed class ScriptBudgetRewriter(SemanticModel model) : CSharpSyntaxRewriter
{
    public const string BudgetVariable = "__budget";

    private static StatementSyntax Tick() =>
        SyntaxFactory.ParseStatement($"{BudgetVariable}.Tick();").WithTrailingTrivia(SyntaxFactory.ElasticLineFeed);

    private static StatementSyntax WithTick(StatementSyntax body) =>
        body is BlockSyntax block
            ? block.WithStatements(block.Statements.Insert(0, Tick()))
            : SyntaxFactory.Block(Tick(), body);

    private static BlockSyntax WithTick(BlockSyntax block) => block.WithStatements(block.Statements.Insert(0, Tick()));

    // ---------------------------------------------------------------- loops

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
        return visited.WithStatement(WithTick(visited.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return visited.WithStatement(WithTick(visited.Statement));
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
        return visited.WithStatement(WithTick(visited.Statement));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return visited.WithStatement(WithTick(visited.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return visited.WithStatement(WithTick(visited.Statement));
    }

    // ---------------------------------------------------------------- catch blocks

    public override SyntaxNode? VisitCatchClause(CatchClauseSyntax node)
    {
        var visited = (CatchClauseSyntax)base.VisitCatchClause(node)!;
        return visited.WithBlock(WithTick(visited.Block));
    }

    // ---------------------------------------------------------------- lambdas and anonymous methods

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var returnsValue = LambdaReturnsValue(node);
        var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
        if (visited.Block is { } block) return visited.WithBlock(WithTick(block));
        if (visited.ExpressionBody is { } expression)
            return visited.WithExpressionBody(null).WithBlock(ExpressionBodyToBlock(expression, returnsValue));
        return visited;
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        var returnsValue = LambdaReturnsValue(node);
        var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
        if (visited.Block is { } block) return visited.WithBlock(WithTick(block));
        if (visited.ExpressionBody is { } expression)
            return visited.WithExpressionBody(null).WithBlock(ExpressionBodyToBlock(expression, returnsValue));
        return visited;
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        var visited = (AnonymousMethodExpressionSyntax)base.VisitAnonymousMethodExpression(node)!;
        return visited.WithBlock(WithTick(visited.Block));
    }

    private static BlockSyntax ExpressionBodyToBlock(ExpressionSyntax expression, bool returnsValue)
    {
        StatementSyntax body = expression switch
        {
            ThrowExpressionSyntax thrown => SyntaxFactory.ThrowStatement(thrown.Expression),
            _ when returnsValue => SyntaxFactory.ReturnStatement(expression),
            _ => SyntaxFactory.ExpressionStatement(expression),
        };
        return SyntaxFactory.Block(Tick(), body);
    }

    /// <summary>
    /// True when the lambda body produces a value: not void, and not an async lambda returning plain Task/ValueTask.
    /// </summary>
    private bool LambdaReturnsValue(AnonymousFunctionExpressionSyntax lambda)
    {
        if (model.GetSymbolInfo(lambda).Symbol is not IMethodSymbol method) return true;
        if (method.ReturnsVoid) return false;
        if (method.IsAsync && method.ReturnType is INamedTypeSymbol { IsGenericType: false } returnType)
        {
            var name = returnType.ToDisplayString();
            if (name is "System.Threading.Tasks.Task" or "System.Threading.Tasks.ValueTask") return false;
        }
        return true;
    }
}
