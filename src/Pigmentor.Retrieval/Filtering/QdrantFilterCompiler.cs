using System.Collections;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Qdrant.Client.Grpc;
using Pigmentor.Core.Documents;
using Pigmentor.Core.Filtering;
using Expression = System.Linq.Expressions.Expression;

namespace Pigmentor.Retrieval.Filtering;

/// <summary>
/// Compiles a <see cref="MetadataFilter"/> into a Qdrant filter — both the JSON
/// shape the chapter teaches (<see cref="Compile"/>) and the strongly-typed
/// gRPC <see cref="Filter"/> the <c>QdrantVectorStore</c> passes to the client
/// (<see cref="ToGrpcFilter"/>). Supported expression nodes:
/// <list type="bullet">
///   <item>equality (<c>==</c>) and inequality (<c>!=</c>) on string / int properties</item>
///   <item>range comparisons (<c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>) on int properties</item>
///   <item><c>AndAlso</c> (must) and <c>OrElse</c> (should) composition</item>
///   <item>set membership via <c>Enumerable.Contains</c> / array <c>Contains</c> (match-any)</item>
///   <item>null-checks (<c>== null</c> / <c>!= null</c>) mapped to <c>is_empty</c> / its negation</item>
/// </list>
/// Lives in <c>Pigmentor.Retrieval</c> (alongside the Qdrant adapter that uses
/// the gRPC builder) and references <c>Pigmentor.Core</c> for
/// <see cref="MetadataFilter"/>.
/// </summary>
public sealed class QdrantFilterCompiler
{
    private const string SupportedNodes =
        "Supported: `==`, `!=`, `<`, `<=`, `>`, `>=`, `&&` (must), `||` (should), " +
        "`Enumerable.Contains`/array `Contains` (match-any), and `== null` / `!= null` (is_empty).";

    /// <summary>Compile to the JSON object form Qdrant's REST/gRPC clients accept.</summary>
    public static string Compile(MetadataFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var clause = BuildClause(filter.Predicate.Body, filter.Predicate.Parameters[0]);
        return JsonSerializer.Serialize(clause.ToJsonObject());
    }

    /// <summary>
    /// Compile to the strongly-typed gRPC <see cref="Filter"/> the official
    /// <c>Qdrant.Client</c> passes to <c>SearchAsync</c>. Same operator coverage
    /// as <see cref="Compile"/>; this is the path <c>QdrantVectorStore</c> uses
    /// for a true pre-filter.
    /// </summary>
    public static Filter ToGrpcFilter(MetadataFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var clause = BuildClause(filter.Predicate.Body, filter.Predicate.Parameters[0]);
        return clause.ToGrpcFilter();
    }

    // ----- Intermediate representation ------------------------------------

    /// <summary>
    /// A compiled boolean clause. We build a small IR first so the same tree
    /// can be rendered either as JSON (for the chapter / tests) or as a gRPC
    /// <see cref="Filter"/> (for the live adapter) without re-walking the
    /// expression twice.
    /// </summary>
    private abstract class Clause
    {
        public abstract object ToJsonObject();

        /// <summary>Emit a gRPC <see cref="Filter"/> representing this whole clause.</summary>
        public Filter ToGrpcFilter()
        {
            var filter = new Filter();
            ApplyTo(filter);
            return filter;
        }

        /// <summary>Add this clause's conditions to an enclosing gRPC <see cref="Filter"/>.</summary>
        public abstract void ApplyTo(Filter filter);
    }

    private sealed class AndClause(List<Clause> parts) : Clause
    {
        public override object ToJsonObject() => new { must = parts.Select(p => p.ToJsonObject()).ToList() };

        public override void ApplyTo(Filter filter)
        {
            foreach (var part in parts)
            {
                // A nested boolean clause becomes a sub-filter condition; a leaf
                // condition is added directly to `must`.
                if (part is ConditionClause cc)
                {
                    filter.Must.Add(cc.ToGrpcCondition());
                }
                else
                {
                    filter.Must.Add(new Condition { Filter = part.ToGrpcFilter() });
                }
            }
        }
    }

    private sealed class OrClause(List<Clause> parts) : Clause
    {
        public override object ToJsonObject() => new { should = parts.Select(p => p.ToJsonObject()).ToList() };

        public override void ApplyTo(Filter filter)
        {
            foreach (var part in parts)
            {
                if (part is ConditionClause cc)
                {
                    filter.Should.Add(cc.ToGrpcCondition());
                }
                else
                {
                    filter.Should.Add(new Condition { Filter = part.ToGrpcFilter() });
                }
            }
        }
    }

    /// <summary>A single leaf condition on one payload key.</summary>
    private sealed class ConditionClause(object json, Func<Condition> grpc, bool negate) : Clause
    {
        public override object ToJsonObject() =>
            negate ? new { must_not = new[] { json } } : json;

        public override void ApplyTo(Filter filter)
        {
            if (negate)
            {
                filter.MustNot.Add(grpc());
            }
            else
            {
                filter.Must.Add(grpc());
            }
        }

        /// <summary>The bare gRPC condition (used when nested inside another clause's must/should).</summary>
        public Condition ToGrpcCondition()
        {
            if (!negate)
            {
                return grpc();
            }
            // A negated leaf inside an OR/AND becomes a sub-filter with must_not.
            var sub = new Filter();
            sub.MustNot.Add(grpc());
            return new Condition { Filter = sub };
        }
    }

    // ----- Expression -> IR -----------------------------------------------

    private static Clause BuildClause(Expression node, ParameterExpression param)
    {
        // MetadataFilter.And/.Or compose by wrapping each side in an
        // InvocationExpression -- AndAlso(Invoke(p1, p), Invoke(p2, p)) -- so a
        // composed filter reaches this switch as Invoke nodes it cannot read.
        // Inline them first (substituting our parameter for the inner lambda's)
        // and the rest of the compiler sees the ordinary tree it expects.
        node = InvocationInliner.Inline(node, param);

        switch (node.NodeType)
        {
            // MetadataFilter.All is `_ => true`: a bare constant body, not a
            // comparison. It means "no constraint", which is an empty must list.
            case ExpressionType.Constant when node is ConstantExpression { Value: bool b }:
                return b
                    ? new AndClause([])
                    : throw new NotSupportedException(
                        "A constantly-false filter matches nothing; " +
                        "omit the search instead of compiling it.");

            case ExpressionType.AndAlso:
                {
                    var bin = (BinaryExpression)node;
                    var parts = new List<Clause>();
                    Flatten(bin, ExpressionType.AndAlso, param, parts);
                    return new AndClause(parts);
                }
            case ExpressionType.OrElse:
                {
                    var bin = (BinaryExpression)node;
                    var parts = new List<Clause>();
                    Flatten(bin, ExpressionType.OrElse, param, parts);
                    return new OrClause(parts);
                }
            case ExpressionType.Equal:
            case ExpressionType.NotEqual:
                return BuildEqualityOrNull((BinaryExpression)node, param);
            case ExpressionType.LessThan:
            case ExpressionType.LessThanOrEqual:
            case ExpressionType.GreaterThan:
            case ExpressionType.GreaterThanOrEqual:
                return BuildRange((BinaryExpression)node, param);
            case ExpressionType.Call:
                return BuildContains((MethodCallExpression)node, param);
        }

        throw new NotSupportedException(
            $"Unsupported expression node {node.NodeType}: {node}. {SupportedNodes}");
    }

    /// <summary>
    /// Rewrites <c>Invoke(lambda, arg)</c> nodes into the lambda's body with its
    /// parameters replaced by the outer predicate's parameter.
    /// </summary>
    /// <remarks>
    /// <see cref="MetadataFilter.And"/> and <see cref="MetadataFilter.Or"/> build
    /// their combined predicate as
    /// <c>AndAlso(Invoke(left, p), Invoke(right, p))</c>. Without this pass the
    /// compiler's node switch hits an <c>Invoke</c> it does not handle and throws
    /// <see cref="NotSupportedException"/> — which meant every composed filter
    /// (including every security scope, since <c>SecurityContext.ToFilter</c>
    /// composes with <c>.And()</c>) failed against Qdrant.
    /// </remarks>
    private sealed class InvocationInliner(ParameterExpression target) : ExpressionVisitor
    {
        private readonly Dictionary<ParameterExpression, Expression> _substitutions = [];

        public static Expression Inline(Expression node, ParameterExpression target)
            => new InvocationInliner(target).Visit(node);

        protected override Expression VisitInvocation(InvocationExpression node)
        {
            // Only inline when the invocation target is a lambda we can see
            // through; anything else (a compiled delegate in a variable, say)
            // is left alone so the node switch reports it honestly.
            if (node.Expression is not LambdaExpression lambda)
            {
                return base.VisitInvocation(node);
            }

            for (var i = 0; i < lambda.Parameters.Count; i++)
            {
                // Arguments to these invocations are the outer parameter itself;
                // map the inner lambda's parameter onto our target so the leaf
                // builders see a single consistent parameter.
                _substitutions[lambda.Parameters[i]] =
                    i < node.Arguments.Count ? Visit(node.Arguments[i]) : target;
            }

            return Visit(lambda.Body);
        }

        protected override Expression VisitParameter(ParameterExpression node)
            => _substitutions.TryGetValue(node, out var replacement) ? replacement : node;
    }

    /// <summary>Collapse a chain of same-operator binaries into a flat clause list.</summary>
    private static void Flatten(
        BinaryExpression bin,
        ExpressionType op,
        ParameterExpression param,
        List<Clause> parts)
    {
        AddSide(bin.Left, op, param, parts);
        AddSide(bin.Right, op, param, parts);
    }

    private static void AddSide(
        Expression side,
        ExpressionType op,
        ParameterExpression param,
        List<Clause> parts)
    {
        if (side.NodeType == op)
        {
            Flatten((BinaryExpression)side, op, param, parts);
        }
        else
        {
            parts.Add(BuildClause(side, param));
        }
    }

    private static ConditionClause BuildEqualityOrNull(BinaryExpression bin, ParameterExpression param)
    {
        var negate = bin.NodeType == ExpressionType.NotEqual;

        if (TryExtractMember(bin.Left, param, out var prop) && TryExtractValue(bin.Right, out var value) ||
            TryExtractMember(bin.Right, param, out prop) && TryExtractValue(bin.Left, out value))
        {
            var key = ToPayloadKey(prop);
            if (value is null)
            {
                // `== null` -> is_empty ; `!= null` -> NOT is_empty.
                object jsonNull = new { is_empty = new { key } };
                return new ConditionClause(
                    jsonNull,
                    () => new Condition { IsEmpty = new IsEmptyCondition { Key = key } },
                    negate);
            }

            object json = new { key, match = new { value } };
            return new ConditionClause(json, () => MatchCondition(key, value), negate);
        }

        throw new NotSupportedException(
            $"Unsupported equality expression {bin}. {SupportedNodes}");
    }

    private static ConditionClause BuildRange(BinaryExpression bin, ParameterExpression param)
    {
        // Normalize so the member is on the left and the constant on the right.
        Expression memberSide = bin.Left, valueSide = bin.Right;
        var nodeType = bin.NodeType;
        if (!(memberSide is MemberExpression me && me.Expression == param))
        {
            (memberSide, valueSide) = (bin.Right, bin.Left);
            nodeType = Flip(nodeType);
        }

        if (TryExtractMember(memberSide, param, out var prop) &&
            TryExtractValue(valueSide, out var value) &&
            value is not null)
        {
            var key = ToPayloadKey(prop);
            var d = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            // Range bounds: gte/lte (inclusive) or gt/lt (exclusive).
            double? gte = null, gt = null, lte = null, lt = null;
            switch (nodeType)
            {
                case ExpressionType.GreaterThanOrEqual: gte = d; break;
                case ExpressionType.GreaterThan: gt = d; break;
                case ExpressionType.LessThanOrEqual: lte = d; break;
                case ExpressionType.LessThan: lt = d; break;
            }

            object json = new
            {
                key,
                range = BuildRangeJson(gte, gt, lte, lt),
            };
            return new ConditionClause(
                json,
                () => RangeCondition(key, gte, gt, lte, lt),
                negate: false);
        }

        throw new NotSupportedException(
            $"Unsupported range expression {bin}. {SupportedNodes}");
    }

    private static ConditionClause BuildContains(MethodCallExpression call, ParameterExpression param)
    {
        // Shapes the C# compiler produces for `coll.Contains(m.Prop)`:
        //   List<T>.Contains(m.Prop)                       -> instance call, 1 arg
        //   Enumerable.Contains(coll, m.Prop)              -> static, 2 args
        //   MemoryExtensions.Contains(op_Implicit(T[]), m.Prop) -> static span ext, 2 args
        Expression? memberArg = null;
        Expression? collectionExpr = null;

        if (call.Method.Name == "Contains")
        {
            if (call.Object is not null && call.Arguments.Count == 1)
            {
                collectionExpr = call.Object;
                memberArg = call.Arguments[0];
            }
            else if (call.Object is null && call.Arguments.Count == 2)
            {
                collectionExpr = call.Arguments[0];
                memberArg = call.Arguments[1];
            }
        }

        // Unwrap an implicit span conversion (op_Implicit(Convert(array, T[]))) so
        // we evaluate the underlying array — a ReadOnlySpan<T> cannot be boxed.
        collectionExpr = UnwrapSpanConversion(collectionExpr);

        if (memberArg is not null &&
            collectionExpr is not null &&
            TryExtractMember(memberArg, param, out var prop) &&
            TryExtractValue(collectionExpr, out var raw) &&
            raw is IEnumerable enumerable && raw is not string)
        {
            var key = ToPayloadKey(prop);
            var values = enumerable.Cast<object?>().Where(v => v is not null).Select(v => v!).ToList();
            object json = new { key, match = new { any = values } };
            return new ConditionClause(json, () => MatchAnyCondition(key, values), negate: false);
        }

        throw new NotSupportedException(
            $"Unsupported method call {call.Method.Name} in {call}. {SupportedNodes}");
    }

    /// <summary>
    /// Strips an implicit <c>ReadOnlySpan&lt;T&gt;</c> conversion the compiler inserts
    /// for <c>T[].Contains(...)</c> (which binds to <c>MemoryExtensions.Contains</c>),
    /// returning the underlying array/collection expression so it can be evaluated.
    /// </summary>
    private static Expression? UnwrapSpanConversion(Expression? expr)
    {
        if (expr is MethodCallExpression { Method.Name: "op_Implicit", Arguments.Count: 1 } conv)
        {
            expr = conv.Arguments[0];
        }
        if (expr is UnaryExpression { NodeType: ExpressionType.Convert } u)
        {
            expr = u.Operand;
        }
        return expr;
    }

    // ----- gRPC condition builders ----------------------------------------

    private static Condition MatchCondition(string key, object value) => value switch
    {
        string s => new Condition { Field = new FieldCondition { Key = key, Match = new Match { Keyword = s } } },
        bool b => new Condition { Field = new FieldCondition { Key = key, Match = new Match { Boolean = b } } },
        _ => new Condition
        {
            Field = new FieldCondition
            {
                Key = key,
                Match = new Match { Integer = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) },
            },
        },
    };

    private static Condition MatchAnyCondition(string key, List<object> values)
    {
        var match = new Match();
        if (values.Count > 0 && values[0] is string)
        {
            match.Keywords = new RepeatedStrings();
            match.Keywords.Strings.AddRange(values.Select(v => (string)v));
        }
        else
        {
            match.Integers = new RepeatedIntegers();
            match.Integers.Integers.AddRange(
                values.Select(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new Condition { Field = new FieldCondition { Key = key, Match = match } };
    }

    private static Condition RangeCondition(string key, double? gte, double? gt, double? lte, double? lt)
    {
        var range = new Qdrant.Client.Grpc.Range();
        if (gte is { } a) { range.Gte = a; }
        if (gt is { } b) { range.Gt = b; }
        if (lte is { } c) { range.Lte = c; }
        if (lt is { } d) { range.Lt = d; }
        return new Condition { Field = new FieldCondition { Key = key, Range = range } };
    }

    private static Dictionary<string, double> BuildRangeJson(double? gte, double? gt, double? lte, double? lt)
    {
        var dict = new Dictionary<string, double>(StringComparer.Ordinal);
        if (gte is { } a) { dict["gte"] = a; }
        if (gt is { } b) { dict["gt"] = b; }
        if (lte is { } c) { dict["lte"] = c; }
        if (lt is { } d) { dict["lt"] = d; }
        return dict;
    }

    private static ExpressionType Flip(ExpressionType t) => t switch
    {
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        _ => t,
    };

    // ----- shared member/value extraction (unchanged from Phase 3) ---------

    private static bool TryExtractMember(Expression expr, ParameterExpression param, out string property)
    {
        // Unwrap Convert nodes (e.g. nullable lifts) so `(int)m.FiscalYear` still matches.
        if (expr is UnaryExpression { NodeType: ExpressionType.Convert } u)
        {
            expr = u.Operand;
        }
        if (expr is MemberExpression me && me.Expression == param)
        {
            property = me.Member.Name;
            return true;
        }
        property = "";
        return false;
    }

    private static bool TryExtractValue(Expression expr, out object? value)
    {
        if (expr is UnaryExpression { NodeType: ExpressionType.Convert } u)
        {
            expr = u.Operand;
        }
        if (expr is ConstantExpression ce)
        {
            value = ce.Value;
            return true;
        }
        if (expr is MemberExpression me && me.Expression is ConstantExpression target)
        {
            // Capture from a closure (e.g. local variable in test).
            value = me.Member switch
            {
                System.Reflection.FieldInfo f => f.GetValue(target.Value),
                System.Reflection.PropertyInfo p => p.GetValue(target.Value),
                _ => null,
            };
            return true;
        }
        // Any other closed-over subtree (captured array/list member, an implicit
        // span conversion like `string[].Contains` binds to MemoryExtensions, a
        // field access on a captured value, …). Evaluate it as long as it does
        // not reference the lambda parameter.
        if (!ReferencesParameter(expr))
        {
            try
            {
                value = Expression.Lambda(Expression.Convert(expr, typeof(object))).Compile().DynamicInvoke();
                return true;
            }
            catch (InvalidOperationException)
            {
                value = null;
                return false;
            }
        }
        value = null;
        return false;
    }

    /// <summary>True if <paramref name="expr"/> references any lambda parameter (so it cannot be a constant).</summary>
    private static bool ReferencesParameter(Expression expr)
    {
        var finder = new ParameterFinder();
        finder.Visit(expr);
        return finder.Found;
    }

    private sealed class ParameterFinder : ExpressionVisitor
    {
        public bool Found { get; private set; }

        protected override Expression VisitParameter(ParameterExpression node)
        {
            Found = true;
            return base.VisitParameter(node);
        }
    }

    /// <summary>
    /// Map a CLR property name to the Qdrant payload key used by
    /// <c>QdrantVectorStore</c> (snake_case, see Ch 6).
    /// </summary>
    private static string ToPayloadKey(string property) => property switch
    {
        nameof(DocumentMetadata.Department) => "department",
        nameof(DocumentMetadata.Office) => "office",
        nameof(DocumentMetadata.ConfidentialityLevel) => "confidentiality",
        nameof(DocumentMetadata.DocumentType) => "document_type",
        nameof(DocumentMetadata.FiscalYear) => "fiscal_year",
        nameof(DocumentMetadata.Author) => "author",
        nameof(DocumentMetadata.Silo) => "silo",
        nameof(DocumentMetadata.Title) => "title",
        nameof(DocumentMetadata.Id) => "document_id",
        _ => ToSnakeCase(property),
    };

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                sb.Append('_');
            }
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
