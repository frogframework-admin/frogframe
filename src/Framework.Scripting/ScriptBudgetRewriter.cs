using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Framework.Scripting;

public sealed class ScriptBudgetRewriter : CSharpSyntaxRewriter
{
    private static readonly StatementSyntax TickStatement =
        SyntaxFactory.ParseStatement("__budget.Tick();\n");

    private StatementSyntax InjectBudgetTick(StatementSyntax body)
    {
        if (body is BlockSyntax block)
        {
            var newStatements = block.Statements.Insert(0, TickStatement);
            return block.WithStatements(newStatements);
        }

        return SyntaxFactory.Block(TickStatement, body);
    }

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
        return visited.WithStatement(InjectBudgetTick(visited.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return visited.WithStatement(InjectBudgetTick(visited.Statement));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return visited.WithStatement(InjectBudgetTick(visited.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return visited.WithStatement(InjectBudgetTick(visited.Statement));
    }
}
