namespace Rowvane.Gate.Readers.Csv;

/// <summary>
/// Detects the delimiter (and, for single-record files, the presence of a header row)
/// from a sample of the file. Explicit ruleset settings always win; sniffing only fills
/// the gaps, so behavior is deterministic once an author pins the options.
/// </summary>
public static class CsvSniffer
{
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>
    /// Picks the candidate delimiter with the highest, most consistent count outside
    /// quotes across the sample lines.
    /// </summary>
    public static char DetectDelimiter(IReadOnlyList<string> sampleLines)
    {
        var best = ',';
        var bestScore = -1d;
        foreach (var candidate in Candidates)
        {
            var counts = sampleLines
                .Where(l => l.Length > 0)
                .Select(l => CountOutsideQuotes(l, candidate))
                .Where(c => c > 0)
                .ToArray();
            if (counts.Length == 0)
            {
                continue;
            }

            // Score: how many lines have the delimiter, weighted by consistency of counts.
            var average = counts.Average();
            var variance = counts.Select(c => Math.Pow(c - average, 2)).Average();
            var score = counts.Length * average / (1 + variance);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// A first row is a header when at least half of the expected field names appear in it.
    /// </summary>
    public static bool LooksLikeHeader(IReadOnlyList<string> firstRow, IEnumerable<string> expectedFieldNames)
    {
        var expected = new HashSet<string>(expectedFieldNames, StringComparer.OrdinalIgnoreCase);
        if (expected.Count == 0)
        {
            return false;
        }

        var matches = firstRow.Count(cell => expected.Contains(cell.Trim()));
        return matches * 2 >= expected.Count;
    }

    private static int CountOutsideQuotes(string line, char delimiter)
    {
        var count = 0;
        var inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == delimiter && !inQuotes)
            {
                count++;
            }
        }

        return count;
    }
}
