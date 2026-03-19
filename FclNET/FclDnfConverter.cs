using System.Collections.Immutable;

using FclNET.Ast;

namespace FclNET;

/// <summary>
/// Converts FCL expressions to Disjunctive Normal Form (DNF) and provides
/// utilities to check DNF shape and extract groups.
/// <para>
/// DNF structure: <c>OR( AND(lit₁, lit₂, …), AND(lit₃, …), … )</c>
/// where each literal is an <see cref="FclCondition"/> or a
/// <see cref="FclNotExpression"/> wrapping an <see cref="FclCondition"/>.
/// </para>
/// </summary>
internal static class FclDnfConverter
{
    // ─────────────────────────────────────────────────────
    //  IsDnf — check whether an expression is already in DNF
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> if <paramref name="expr"/> is already in DNF shape.
    /// </summary>
    public static bool IsDnf(FclExpression expr) => IsDnfCore(Unwrap(expr));

    /// <summary>
    /// Core DNF-shape check after group unwrapping.
    /// </summary>
    private static bool IsDnfCore(FclExpression expr) => expr switch
    {
        // A single literal is trivially DNF.
        FclCondition => true,

        // NOT(condition) is a literal.
        FclNotExpression { Operand: FclCondition } => true,
        // NOT wrapping a group — unwrap and recheck.
        FclNotExpression { Operand: FclGroupExpression g } => IsDnfCore(new FclNotExpression(g.Inner, g.Inner.Span)),
        // NOT(anything else) is NOT DNF (e.g. NOT(AND(...)), NOT(OR(...))).
        FclNotExpression => false,

        // AND whose operands are all literals.
        FclAndExpression and => and.Operands.All(op => IsLiteral(Unwrap(op))),

        // OR whose operands are all (AND-of-literals or literals).
        FclOrExpression or => or.Operands.All(op => IsDnfClause(Unwrap(op))),

        // Parenthesized — should have been unwrapped, but guard anyway.
        FclGroupExpression g => IsDnfCore(g.Inner),

        // Error expressions are not valid DNF.
        _ => false
    };

    /// <summary>
    /// A DNF clause is either a literal or an AND-of-literals.
    /// </summary>
    private static bool IsDnfClause(FclExpression expr) => expr switch
    {
        FclCondition => true,
        FclNotExpression { Operand: FclCondition } => true,
        FclNotExpression { Operand: FclGroupExpression g } => IsDnfClause(new FclNotExpression(g.Inner, g.Inner.Span)),
        FclAndExpression and => and.Operands.All(op => IsLiteral(Unwrap(op))),
        FclGroupExpression g => IsDnfClause(g.Inner),
        _ => false
    };

    /// <summary>
    /// A literal is an <see cref="FclCondition"/> or <c>NOT(FclCondition)</c>.
    /// </summary>
    private static bool IsLiteral(FclExpression expr) => expr switch
    {
        FclCondition => true,
        FclNotExpression { Operand: FclCondition } => true,
        FclNotExpression { Operand: FclGroupExpression g } => IsLiteral(new FclNotExpression(g.Inner, g.Inner.Span)),
        FclGroupExpression g => IsLiteral(g.Inner),
        _ => false
    };

    // ─────────────────────────────────────────────────────
    //  ToDnf — convert an expression to DNF
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Converts <paramref name="expr"/> to Disjunctive Normal Form.
    /// Returns <c>null</c> if the resulting clause count would exceed
    /// <paramref name="maxClauses"/> (exponential blowup guard).
    /// </summary>
    /// <param name="expr">A validated FCL expression.</param>
    /// <param name="maxClauses">Maximum number of allowed OR-clauses (default 256).</param>
    public static FclExpression? ToDnf(FclExpression expr, int maxClauses = 256)
    {
        // Step 1: Unwrap groups, eliminate double negations, push NOT inward (De Morgan).
        var normalized = PushNegations(Unwrap(expr));

        // Step 2: Distribute AND over OR to reach DNF.
        var dnf = Distribute(normalized);

        // Step 3: Flatten nested OR/AND chains.
        dnf = Flatten(dnf);

        // Step 4: Blowup guard — count the number of OR-clauses.
        if (CountClauses(dnf) > maxClauses)
            return null;

        return dnf;
    }

    // ─────────────────────────────────────────────────────
    //  ExtractDnfGroups
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Given an expression already in DNF, extracts the groups as a
    /// list-of-lists: outer = OR groups, inner = AND literals per group.
    /// </summary>
    /// <param name="dnfExpr">An expression in DNF form.</param>
    public static List<List<FclExpression>> ExtractDnfGroups(FclExpression dnfExpr)
    {
        var expr = Unwrap(dnfExpr);
        var groups = new List<List<FclExpression>>();

        // Collect top-level OR operands (or the whole expression if no OR).
        var clauses = expr is FclOrExpression or
            ? or.Operands.Select(Unwrap).ToList()
            : [expr];

        foreach (var clause in clauses)
        {
            var literals = new List<FclExpression>();
            if (clause is FclAndExpression and)
            {
                foreach (var op in and.Operands)
                    literals.Add(Unwrap(op));
            }
            else
            {
                // Single literal clause.
                literals.Add(clause);
            }
            groups.Add(literals);
        }

        return groups;
    }

    // ─────────────────────────────────────────────────────
    //  Transformation helpers
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// Recursively unwraps <see cref="FclGroupExpression"/> nodes.
    /// </summary>
    private static FclExpression Unwrap(FclExpression expr) =>
        expr is FclGroupExpression g ? Unwrap(g.Inner) : expr;

    /// <summary>
    /// Eliminates double negations and applies De Morgan's laws to push
    /// NOT inward until it sits directly on <see cref="FclCondition"/> nodes.
    /// </summary>
    private static FclExpression PushNegations(FclExpression expr) => expr switch
    {
        FclCondition => expr,

        FclGroupExpression g => PushNegations(g.Inner),

        FclNotExpression not => PushNot(Unwrap(not.Operand)),

        FclAndExpression and => new FclAndExpression(
            [.. and.Operands.Select(op => PushNegations(Unwrap(op)))],
            SourceSpan.None),

        FclOrExpression or => new FclOrExpression(
            [.. or.Operands.Select(op => PushNegations(Unwrap(op)))],
            SourceSpan.None),

        // Error or unknown — pass through.
        _ => expr
    };

    /// <summary>
    /// Handles the negation of an expression (the operand of a NOT node):
    /// eliminates double negation and applies De Morgan's laws.
    /// </summary>
    private static FclExpression PushNot(FclExpression operand)
    {
        var inner = Unwrap(operand);

        return inner switch
        {
            // NOT(condition) → literal, done.
            FclCondition => new FclNotExpression(inner, SourceSpan.None),

            // NOT(NOT(x)) → x  (double negation elimination)
            FclNotExpression not2 => PushNegations(Unwrap(not2.Operand)),

            // NOT(A AND B) → NOT(A) OR NOT(B)  (De Morgan)
            FclAndExpression and => new FclOrExpression(
                [.. and.Operands.Select(op => PushNot(Unwrap(op)))],
                SourceSpan.None),

            // NOT(A OR B) → NOT(A) AND NOT(B)  (De Morgan)
            FclOrExpression or => new FclAndExpression(
                [.. or.Operands.Select(op => PushNot(Unwrap(op)))],
                SourceSpan.None),

            // NOT(group) — unwrap handled above, but guard.
            FclGroupExpression g => PushNot(g.Inner),

            // Unknown — wrap in NOT.
            _ => new FclNotExpression(inner, SourceSpan.None)
        };
    }

    /// <summary>
    /// Distributes AND over OR to produce DNF.
    /// <c>A AND (B OR C)</c> becomes <c>(A AND B) OR (A AND C)</c>.
    /// </summary>
    private static FclExpression Distribute(FclExpression expr) => expr switch
    {
        // Literals are already in DNF.
        FclCondition => expr,
        FclNotExpression { Operand: FclCondition } => expr,

        // OR: distribute each operand independently, then flatten.
        FclOrExpression or => new FclOrExpression(
            [.. or.Operands.Select(Distribute)],
            SourceSpan.None),

        // AND: this is where the actual distribution happens.
        FclAndExpression and => DistributeAnd(and),

        // Anything else (error, etc.) — pass through.
        _ => expr
    };

    /// <summary>
    /// Distributes an AND expression over any OR operands to achieve DNF.
    /// <para>
    /// Conceptually: AND(sets₁, sets₂, …) where each setsᵢ is a list of
    /// OR-alternatives. The result is the Cartesian product joined by AND,
    /// wrapped in OR.
    /// </para>
    /// </summary>
    private static FclExpression DistributeAnd(FclAndExpression and)
    {
        // First, recursively distribute each operand.
        var distributed = and.Operands.Select(Distribute).ToList();

        // Represent each operand as a list of OR-alternatives.
        // If an operand is OR(a, b, c), the alternatives are [a, b, c].
        // Otherwise, the single operand is [operand].
        var alternativeSets = distributed
            .Select(op => op is FclOrExpression or
                ? or.Operands.AsEnumerable().ToList()
                : new List<FclExpression> { op })
            .ToList();

        // Cartesian product of all alternative sets.
        var product = CartesianProduct(alternativeSets);

        // Each product element becomes an AND-clause.
        var clauses = product
            .Select(combo =>
            {
                // Flatten: if a combo element is itself AND, inline its operands.
                var flat = new List<FclExpression>();
                foreach (var item in combo)
                {
                    if (item is FclAndExpression inner)
                        flat.AddRange(inner.Operands);
                    else
                        flat.Add(item);
                }

                return flat.Count == 1
                    ? flat[0]
                    : (FclExpression)new FclAndExpression([.. flat], SourceSpan.None);
            })
            .ToImmutableArray();

        return clauses.Length == 1
            ? clauses[0]
            : new FclOrExpression(clauses, SourceSpan.None);
    }

    /// <summary>
    /// Computes the Cartesian product of a list of lists.
    /// </summary>
    private static List<List<FclExpression>> CartesianProduct(List<List<FclExpression>> sets)
    {
        var result = new List<List<FclExpression>> { new() };

        foreach (var set in sets)
        {
            var newResult = new List<List<FclExpression>>();
            foreach (var existing in result)
            {
                foreach (var item in set)
                {
                    var combo = new List<FclExpression>(existing) { item };
                    newResult.Add(combo);
                }
            }
            result = newResult;
        }

        return result;
    }

    /// <summary>
    /// Flattens nested OR-of-OR and AND-of-AND chains into single-level
    /// chains with all operands collected.
    /// </summary>
    private static FclExpression Flatten(FclExpression expr) => expr switch
    {
        FclOrExpression or => FlattenOr(or),
        FclAndExpression and => FlattenAnd(and),
        FclNotExpression not => new FclNotExpression(Flatten(not.Operand), SourceSpan.None),
        _ => expr
    };

    private static FclExpression FlattenOr(FclOrExpression or)
    {
        var flat = new List<FclExpression>();
        CollectOrOperands(or, flat);
        var flattened = flat.Select(Flatten).ToImmutableArray();
        return flattened.Length == 1
            ? flattened[0]
            : new FclOrExpression(flattened, SourceSpan.None);
    }

    private static void CollectOrOperands(FclExpression expr, List<FclExpression> result)
    {
        if (expr is FclOrExpression or)
        {
            foreach (var op in or.Operands)
                CollectOrOperands(op, result);
        }
        else
        {
            result.Add(expr);
        }
    }

    private static FclExpression FlattenAnd(FclAndExpression and)
    {
        var flat = new List<FclExpression>();
        CollectAndOperands(and, flat);
        var flattened = flat.Select(Flatten).ToImmutableArray();
        return flattened.Length == 1
            ? flattened[0]
            : new FclAndExpression(flattened, SourceSpan.None);
    }

    private static void CollectAndOperands(FclExpression expr, List<FclExpression> result)
    {
        if (expr is FclAndExpression and)
        {
            foreach (var op in and.Operands)
                CollectAndOperands(op, result);
        }
        else
        {
            result.Add(expr);
        }
    }

    /// <summary>
    /// Counts the number of top-level OR-clauses in the expression.
    /// Used for the blowup guard.
    /// </summary>
    private static int CountClauses(FclExpression expr) => expr switch
    {
        FclOrExpression or => or.Operands.Length,
        _ => 1
    };
}
