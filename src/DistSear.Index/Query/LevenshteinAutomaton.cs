namespace DistSear.Index.Query;

/// <summary>
/// Bounded Levenshtein matcher driven one character at a time.
///
/// The state is a row of the edit-distance dynamic-programming table, which is exactly the set of
/// reachable NFA states for the classic Levenshtein automaton. Feeding characters advances the row;
/// because the row is a function of the consumed prefix alone, a traversal of a sorted term
/// dictionary can compute it once per shared prefix and reuse it for every term underneath.
///
/// The pruning property is what makes this worth doing: when every entry in the row already exceeds
/// the edit budget, no extension of that prefix can match, so an entire range of the dictionary is
/// skipped without being examined.
/// </summary>
public sealed class LevenshteinAutomaton
{
    private readonly string _pattern;
    private readonly int _maxEdits;
    private readonly bool _transpositions;

    public LevenshteinAutomaton(string pattern, int maxEdits, bool transpositions = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxEdits);

        _pattern = pattern;
        _maxEdits = maxEdits;
        _transpositions = transpositions;
    }

    public int MaxEdits => _maxEdits;

    /// <summary>A DP row plus the prefix that produced it.</summary>
    public readonly struct State
    {
        internal State(int[] row, int[]? previousRow, char lastCharacter, int length)
        {
            Row = row;
            PreviousRow = previousRow;
            LastCharacter = lastCharacter;
            Length = length;
        }

        internal int[] Row { get; }

        /// <summary>Kept so Damerau transposition can look back one character.</summary>
        internal int[]? PreviousRow { get; }

        internal char LastCharacter { get; }

        internal int Length { get; }
    }

    /// <summary>Row for the empty prefix: deleting <c>i</c> characters of the pattern costs <c>i</c>.</summary>
    public State Start()
    {
        var row = new int[_pattern.Length + 1];

        for (var i = 0; i <= _pattern.Length; i++)
        {
            row[i] = i;
        }

        return new State(row, null, '\0', 0);
    }

    public State Step(in State state, char character)
    {
        var previous = state.Row;
        var row = new int[_pattern.Length + 1];

        // First column: the candidate is one character longer, so cost grows with its length.
        row[0] = state.Length + 1;

        for (var i = 1; i <= _pattern.Length; i++)
        {
            var substitution = previous[i - 1] + (_pattern[i - 1] == character ? 0 : 1);
            var insertion = row[i - 1] + 1;
            var deletion = previous[i] + 1;

            var cost = Math.Min(substitution, Math.Min(insertion, deletion));

            // Damerau transposition: the pattern's two characters appear swapped in the candidate.
            if (_transpositions
                && i > 1
                && state.PreviousRow is { } beforePrevious
                && _pattern[i - 1] == state.LastCharacter
                && _pattern[i - 2] == character)
            {
                cost = Math.Min(cost, beforePrevious[i - 2] + 1);
            }

            row[i] = cost;
        }

        return new State(row, previous, character, state.Length + 1);
    }

    /// <summary>True when the consumed prefix is itself within the edit budget of the whole pattern.</summary>
    public bool IsMatch(in State state) => state.Row[^1] <= _maxEdits;

    /// <summary>
    /// True when some extension of the consumed prefix could still match. False means the whole
    /// subtree of terms sharing this prefix can be skipped.
    /// </summary>
    public bool CanMatch(in State state)
    {
        foreach (var cost in state.Row)
        {
            if (cost <= _maxEdits)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Edit distance between the pattern and the consumed prefix, capped by the budget.</summary>
    public int Distance(in State state) => state.Row[^1];

    /// <summary>
    /// Reference implementation kept for testing: full Damerau-Levenshtein distance with no bound.
    /// The automaton must agree with this for every term it accepts.
    /// </summary>
    public static int Distance(string a, string b, bool transpositions = true)
    {
        var rows = new int[a.Length + 1][];

        for (var i = 0; i <= a.Length; i++)
        {
            rows[i] = new int[b.Length + 1];
            rows[i][0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            rows[0][j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;

                var value = Math.Min(
                    rows[i - 1][j] + 1,
                    Math.Min(rows[i][j - 1] + 1, rows[i - 1][j - 1] + cost));

                if (transpositions
                    && i > 1
                    && j > 1
                    && a[i - 1] == b[j - 2]
                    && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, rows[i - 2][j - 2] + 1);
                }

                rows[i][j] = value;
            }
        }

        return rows[a.Length][b.Length];
    }
}
