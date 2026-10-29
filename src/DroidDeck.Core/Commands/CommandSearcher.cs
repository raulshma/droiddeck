namespace DroidDeck.Core.Commands;

/// <summary>A ranked search hit.</summary>
/// <param name="Command">The matching command.</param>
/// <param name="Score">The match score; higher matches rank earlier.</param>
public sealed record CommandSearchResult(CommandDefinition Command, int Score);

/// <summary>
/// Ranks commands against a query using the fuzzy matcher over title, keywords,
/// category, and description. Title matches weigh the most.
/// </summary>
public static class CommandSearcher
{
    private const int TitleBonus = 600;
    private const int KeywordBonus = 400;
    private const int CategoryBonus = 200;
    private const int DescriptionBonus = 100;

    /// <summary>
    /// Returns the enabled commands matching the query, best first. An empty
    /// query returns every enabled command in registration order with score zero.
    /// </summary>
    public static IReadOnlyList<CommandSearchResult> Search(
        IReadOnlyList<CommandDefinition> commands,
        string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return commands
                .Where(command => command.IsEnabled)
                .Select(command => new CommandSearchResult(command, 0))
                .ToArray();
        }

        var results = new List<CommandSearchResult>();
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            if (!command.IsEnabled)
            {
                continue;
            }

            var score = Rank(command, query);
            if (score > 0)
            {
                results.Add(new CommandSearchResult(command, score));
            }
        }

        return results
            .OrderByDescending(result => result.Score)
            .ThenBy(result => IndexOf(commands, result.Command))
            .ToArray();
    }

    private static int Rank(CommandDefinition command, string query)
    {
        var best = Field(FuzzyMatcher.Match(query, command.Title), TitleBonus);
        foreach (var keyword in command.Keywords)
        {
            best = Math.Max(best, Field(FuzzyMatcher.Match(query, keyword), KeywordBonus));
        }

        best = Math.Max(best, Field(FuzzyMatcher.Match(query, command.Category), CategoryBonus));
        if (command.Description.Length > 0)
        {
            best = Math.Max(best, Field(FuzzyMatcher.Match(query, command.Description), DescriptionBonus));
        }

        return best;
    }

    private static int Field(FuzzyMatch? match, int bonus) => match is null ? 0 : match.Score + bonus;

    private static int IndexOf(IReadOnlyList<CommandDefinition> commands, CommandDefinition command)
    {
        for (var index = 0; index < commands.Count; index++)
        {
            if (ReferenceEquals(commands[index], command))
            {
                return index;
            }
        }

        return int.MaxValue;
    }
}
