namespace DistSear.Analysis;

/// <summary>
/// One term produced by the analysis chain.
/// </summary>
/// <param name="Term">The indexed form, after every filter has run.</param>
/// <param name="Position">
/// Ordinal position in the token stream. Phrase queries compare these, so filters must preserve
/// them: dropping a stopword leaves a gap (which correctly prevents a phrase from matching across
/// the removed word), and a synonym is emitted at the same position as the token it replaces.
/// </param>
/// <param name="StartOffset">Start index in the original text. Highlighting depends on this.</param>
/// <param name="EndOffset">End index (exclusive) in the original text.</param>
public readonly record struct Token(string Term, int Position, int StartOffset, int EndOffset)
{
    public Token WithTerm(string term) => this with { Term = term };
}
