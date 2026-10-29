namespace DroidDeck.Core.Commands;

/// <summary>A successful fuzzy match with the score and the matched positions.</summary>
/// <param name="Score">Positive score; higher matches rank earlier.</param>
/// <param name="MatchedIndices">Character positions in the candidate text, ascending.</param>
public sealed record FuzzyMatch(int Score, IReadOnlyList<int> MatchedIndices);

/// <summary>
/// Case-insensitive subsequence matcher with match-quality scoring. Exact matches
/// rank above prefixes, prefixes above substrings, and substrings above scattered
/// subsequences; contiguous runs and word starts score higher than gaps.
/// </summary>
public static class FuzzyMatcher
{
    private const int ExactScore = 10000;
    private const int PrefixScore = 5000;
    private const int SubstringScore = 4000;
    private const int SubsequenceBase = 10;
    private const int WordStartBonus = 25;
    private const int ConsecutiveBonus = 20;
    private const int MaxGapPenalty = 8;

    /// <summary>Scores the query against a candidate, or returns <see langword="null"/> on no match.</summary>
    public static FuzzyMatch? Match(string? query, string? text)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new FuzzyMatch(0, []);
        }

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var loweredQuery = query.Trim().ToLowerInvariant();
        var loweredText = text.ToLowerInvariant();
        if (loweredQuery.Length == 0 || loweredQuery.Length > loweredText.Length)
        {
            return loweredQuery.Length == 0 ? new FuzzyMatch(0, []) : null;
        }

        if (string.Equals(loweredQuery, loweredText, StringComparison.Ordinal))
        {
            return RangedIndices(ExactScore, 0, loweredQuery.Length);
        }

        if (loweredText.StartsWith(loweredQuery, StringComparison.Ordinal))
        {
            return RangedIndices(PrefixScore - (loweredText.Length - loweredQuery.Length), 0, loweredQuery.Length);
        }

        var substring = loweredText.IndexOf(loweredQuery, StringComparison.Ordinal);
        if (substring >= 0)
        {
            return RangedIndices(SubstringScore - substring, substring, loweredQuery.Length);
        }

        return ScoreSubsequence(loweredQuery, text, loweredText);
    }

    private static FuzzyMatch RangedIndices(int score, int start, int length)
    {
        var indices = new int[length];
        for (var index = 0; index < length; index++)
        {
            indices[index] = start + index;
        }

        return new FuzzyMatch(score, indices);
    }

    private static FuzzyMatch? ScoreSubsequence(string loweredQuery, string text, string loweredText)
    {
        var indices = new int[loweredQuery.Length];
        var score = 0;
        var previousIndex = -1;
        for (var queryIndex = 0; queryIndex < loweredQuery.Length; queryIndex++)
        {
            var index = loweredText.IndexOf(loweredQuery[queryIndex], previousIndex + 1);
            if (index < 0)
            {
                return null;
            }

            var charScore = SubsequenceBase;
            if (IsWordStart(text, index))
            {
                charScore += WordStartBonus;
            }

            if (index == previousIndex + 1)
            {
                charScore += ConsecutiveBonus;
            }
            else if (previousIndex >= 0)
            {
                charScore -= Math.Min(index - previousIndex - 1, MaxGapPenalty);
            }

            score += charScore;
            indices[queryIndex] = index;
            previousIndex = index;
        }

        return score > 0 ? new FuzzyMatch(score, indices) : null;
    }

    private static bool IsWordStart(string text, int index)
    {
        if (index == 0)
        {
            return true;
        }

        var previous = text[index - 1];
        if (!char.IsLetterOrDigit(previous))
        {
            return true;
        }

        // Treat a lower-to-upper transition (camelCase humps) as a word start.
        return char.IsLower(previous) && char.IsUpper(text[index]);
    }
}
