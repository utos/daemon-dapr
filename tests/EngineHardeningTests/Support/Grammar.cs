using Acornima;
using Acornima.Ast;

namespace EngineHardeningTests.Support;

public sealed record GrammarViolation(string Code, string Message, int Line, int Column)
{
    public override string ToString() => $"{Code} {Message} @{Line}:{Column}";
}

public sealed class GrammarRejectedException(IReadOnlyList<GrammarViolation> violations)
    : Exception(string.Join("; ", violations))
{
    public IReadOnlyList<GrammarViolation> Violations { get; } = violations;
}

/// <summary>
/// §6.0: the language is a subset of JavaScript by grammar — the minimum a workflow transition
/// needs. An <b>allow-list</b> of syntax-tree node types: anything not named here is refused,
/// including whatever a future parser version adds. Loose on data access (computed keys,
/// destructuring, spread, const helpers), with no loops, classes, prototypes, or functions other
/// than arrows. Iteration therefore only happens over data that already exists, so work is
/// proportional to input size; and no expression can build a prototype chain.
///
/// Checked by the daemon on the tree Jint is about to run; mirrored by the CLI as lint with
/// file:line. Codes are placeholders until the spec assigns them.
/// </summary>
public static class Grammar
{
    public static IReadOnlyList<GrammarViolation> Check(Acornima.Ast.Program program)
    {
        var v = new List<GrammarViolation>();
        foreach (var statement in program.Body)
            Statement(statement, inArrowBody: false, v);
        return v;
    }

    // ---- statements ---------------------------------------------------------------------------

    private static void Statement(Acornima.Ast.Statement node, bool inArrowBody, List<GrammarViolation> v)
    {
        switch (node)
        {
            case VariableDeclaration declaration:
                if (declaration.Kind == VariableDeclarationKind.Var)
                    Reject(v, "G010", "use const or let", node);
                foreach (var declarator in declaration.Declarations)
                {
                    Pattern(declarator.Id, inArrowBody, v);
                    if (declarator.Init is not null) Expression(declarator.Init, inArrowBody, v);
                }
                break;

            case ExpressionStatement statement:
                Expression(statement.Expression, inArrowBody, v);
                break;

            case IfStatement @if:
                Expression(@if.Test, inArrowBody, v);
                Statement(@if.Consequent, inArrowBody, v);
                if (@if.Alternate is not null) Statement(@if.Alternate, inArrowBody, v);
                break;

            case BlockStatement block:
                foreach (var statement in block.Body) Statement(statement, inArrowBody, v);
                break;

            case EmptyStatement:
                break;

            case ReturnStatement ret:
                if (!inArrowBody) Reject(v, "G011", "return only belongs inside an arrow function body", node);
                if (ret.Argument is not null) Expression(ret.Argument, inArrowBody, v);
                break;

            case ForStatement or ForInStatement or ForOfStatement or WhileStatement or DoWhileStatement:
                Reject(v, "G001", "loops are not part of the language; iterate with map, filter, flatMap or reduce", node);
                break;

            case FunctionDeclaration:
                Reject(v, "G002", "function declarations are not part of the language; use a const arrow function", node);
                break;

            case ClassDeclaration:
                Reject(v, "G003", "classes are not part of the language", node);
                break;

            default:
                Reject(v, "G099", $"{node.Type} is not part of the language", node);
                break;
        }
    }

    // ---- binding patterns ---------------------------------------------------------------------

    private static void Pattern(Node node, bool inArrowBody, List<GrammarViolation> v)
    {
        switch (node)
        {
            case Identifier:
                break;

            case ObjectPattern obj:
                foreach (var item in obj.Properties)
                {
                    if (item is RestElement rest) Pattern(rest.Argument, inArrowBody, v);
                    else if (item is Property property)
                    {
                        if (property.Computed) Expression(property.Key, inArrowBody, v);
                        Pattern(property.Value, inArrowBody, v);
                    }
                    else Reject(v, "G099", $"{item.Type} is not part of the language", item);
                }
                break;

            case ArrayPattern array:
                foreach (var element in array.Elements)
                    if (element is not null) Pattern(element, inArrowBody, v);
                break;

            case AssignmentPattern assignment:
                Pattern(assignment.Left, inArrowBody, v);
                Expression(assignment.Right, inArrowBody, v);
                break;

            case RestElement rest:
                Pattern(rest.Argument, inArrowBody, v);
                break;

            case MemberExpression member:
                // Assignment target: `o.n = 5`. Frozen inputs make it a TypeError; locals accept it.
                Expression(member, inArrowBody, v);
                break;

            default:
                Reject(v, "G099", $"{node.Type} is not part of the language", node);
                break;
        }
    }

    // ---- expressions --------------------------------------------------------------------------

    private static void Expression(Node node, bool inArrowBody, List<GrammarViolation> v)
    {
        switch (node)
        {
            case Identifier or Literal:
                break;

            case TemplateLiteral template:
                foreach (var expression in template.Expressions) Expression(expression, inArrowBody, v);
                break;

            case ArrayExpression array:
                foreach (var element in array.Elements)
                {
                    if (element is null) Reject(v, "G012", "array holes are not part of the language", array);
                    else if (element is SpreadElement spread) Expression(spread.Argument, inArrowBody, v);
                    else Expression(element, inArrowBody, v);
                }
                break;

            case ObjectExpression obj:
                foreach (var item in obj.Properties)
                {
                    if (item is SpreadElement spread)
                    {
                        Expression(spread.Argument, inArrowBody, v);
                        continue;
                    }

                    if (item is not Property property)
                    {
                        Reject(v, "G099", $"{item.Type} is not part of the language", item);
                        continue;
                    }

                    if (property.Kind != PropertyKind.Init || property.Method)
                        Reject(v, "G020", "getters, setters and methods are not part of the language", property);

                    var key = property.Computed ? null : property.Key switch
                    {
                        Identifier id => id.Name,
                        StringLiteral s => s.Value,
                        _ => null,
                    };
                    if (key == "__proto__")
                        Reject(v, "G021", "an object literal cannot set its prototype", property);

                    if (property.Computed) Expression(property.Key, inArrowBody, v);
                    Expression(property.Value, inArrowBody, v);
                }
                break;

            case MemberExpression member:
                Expression(member.Object, inArrowBody, v);
                if (member.Computed) Expression(member.Property, inArrowBody, v);
                else if (member.Property is not Identifier)
                    Reject(v, "G099", $"{member.Property.Type} is not part of the language", member.Property);
                break;

            case ChainExpression chain:
                Expression(chain.Expression, inArrowBody, v);
                break;

            case CallExpression call:
                if (call.Callee is Identifier { Name: "Array" or "Object" or "Function" or "eval" } callee)
                    Reject(v, "G041", $"`{callee.Name}()` cannot be called", node);
                Expression(call.Callee, inArrowBody, v);
                Arguments(call.Arguments, inArrowBody, v);
                break;

            case NewExpression @new:
                if (@new.Callee is not Identifier { Name: "Set" or "Map" })
                    Reject(v, "G040", "`new` is limited to Set and Map", node);
                Arguments(@new.Arguments, inArrowBody, v);
                break;

            case ConditionalExpression conditional:
                Expression(conditional.Test, inArrowBody, v);
                Expression(conditional.Consequent, inArrowBody, v);
                Expression(conditional.Alternate, inArrowBody, v);
                break;

            case LogicalExpression logical:
                Expression(logical.Left, inArrowBody, v);
                Expression(logical.Right, inArrowBody, v);
                break;

            case BinaryExpression binary:
                if (binary.Operator is not (
                    Operator.StrictEquality or Operator.StrictInequality or Operator.Equality or Operator.Inequality
                    or Operator.LessThan or Operator.LessThanOrEqual or Operator.GreaterThan or Operator.GreaterThanOrEqual
                    or Operator.Addition or Operator.Subtraction or Operator.Multiplication or Operator.Division
                    or Operator.Remainder or Operator.Exponentiation or Operator.In))
                    Reject(v, "G051", $"operator `{binary.Operator}` is not part of the language", node);
                Expression(binary.Left, inArrowBody, v);
                Expression(binary.Right, inArrowBody, v);
                break;

            // Before UnaryExpression: Acornima models `++`/`--` as a subclass of it.
            case UpdateExpression update:
                Expression(update.Argument, inArrowBody, v);
                break;

            case UnaryExpression unary:
                if (unary.Operator is not (Operator.LogicalNot or Operator.UnaryNegation or Operator.UnaryPlus or Operator.TypeOf))
                    Reject(v, "G050", $"operator `{unary.Operator}` is not part of the language", node);
                Expression(unary.Argument, inArrowBody, v);
                break;

            case AssignmentExpression assignment:
                if (assignment.Operator is not (
                    Operator.Assignment or Operator.AdditionAssignment or Operator.SubtractionAssignment
                    or Operator.MultiplicationAssignment or Operator.DivisionAssignment))
                    Reject(v, "G052", $"operator `{assignment.Operator}` is not part of the language", node);
                Pattern(assignment.Left, inArrowBody, v);
                Expression(assignment.Right, inArrowBody, v);
                break;

            case ArrowFunctionExpression arrow:
                if (arrow.Async) Reject(v, "G031", "async arrow functions are not part of the language", node);
                foreach (var parameter in arrow.Params) Pattern(parameter, inArrowBody: true, v);
                if (arrow.Body is BlockStatement block)
                    foreach (var statement in block.Body) Statement(statement, inArrowBody: true, v);
                else
                    Expression(arrow.Body, inArrowBody: true, v);
                break;

            // Named rejections, so the message says what to use instead.
            case FunctionExpression:
                Reject(v, "G002", "function expressions are not part of the language; use an arrow function", node);
                break;
            case ClassExpression:
                Reject(v, "G003", "classes are not part of the language", node);
                break;
            case ThisExpression:
                Reject(v, "G030", "`this` is not part of the language", node);
                break;
            case YieldExpression or AwaitExpression:
                Reject(v, "G031", "async and generator constructs are not part of the language", node);
                break;
            case ImportExpression or MetaProperty:
                Reject(v, "G032", "modules are not part of the language", node);
                break;
            case SequenceExpression:
                Reject(v, "G035", "comma expressions are not part of the language", node);
                break;

            default:
                Reject(v, "G099", $"{node.Type} is not part of the language", node);
                break;
        }
    }

    private static void Arguments(in NodeList<Expression> arguments, bool inArrowBody, List<GrammarViolation> v)
    {
        foreach (var argument in arguments)
        {
            if (argument is SpreadElement spread) Expression(spread.Argument, inArrowBody, v);
            else Expression(argument, inArrowBody, v);
        }
    }

    private static void Reject(List<GrammarViolation> v, string code, string message, Node node) =>
        v.Add(new GrammarViolation(code, message, node.Location.Start.Line, node.Location.Start.Column));
}
