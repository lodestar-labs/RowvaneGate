using System.Runtime.CompilerServices;
using System.Text;

namespace Rowvane.Gate.Readers.Csv;

/// <summary>
/// Dependency-free RFC 4180 parser: quoted fields, escaped quotes ("" inside a quoted
/// field), delimiters and line breaks inside quotes, CR / LF / CRLF endings. Streams rows
/// without loading the file and reports the line each row starts on, so findings point at
/// the right place in the source. All parser state lives outside the read loop, so it
/// survives buffer refills — preserve that property when changing it.
/// </summary>
public static class CsvParser
{
    public sealed record CsvRow(long LineNumber, IReadOnlyList<string> Fields);

    public static async IAsyncEnumerable<CsvRow> ParseAsync(
        TextReader reader,
        char delimiter = ',',
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var justClosedQuote = false;
        var rowHasData = false;
        long line = 1;
        long rowStartLine = 1;
        var previousWasCarriageReturn = false;

        var buffer = new char[8192];
        int read;

        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (previousWasCarriageReturn)
                {
                    previousWasCarriageReturn = false;
                    if (c == '\n')
                    {
                        continue;
                    }
                }

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        inQuotes = false;
                        justClosedQuote = true;
                    }
                    else if (c is '\r' or '\n')
                    {
                        previousWasCarriageReturn = c == '\r';
                        line++;
                        current.Append('\n');
                    }
                    else
                    {
                        current.Append(c);
                    }

                    continue;
                }

                switch (c)
                {
                    case '"' when justClosedQuote:
                        current.Append('"');
                        inQuotes = true;
                        justClosedQuote = false;
                        break;

                    case '"' when current.Length == 0:
                        inQuotes = true;
                        rowHasData = true;
                        break;

                    case '"':
                        current.Append('"');
                        break;

                    case '\r':
                    case '\n':
                        previousWasCarriageReturn = c == '\r';
                        line++;
                        if (rowHasData || fields.Count > 0 || current.Length > 0)
                        {
                            fields.Add(current.ToString());
                            yield return new CsvRow(rowStartLine, fields.ToArray());
                            fields.Clear();
                            current.Clear();
                        }

                        rowHasData = false;
                        justClosedQuote = false;
                        rowStartLine = line;
                        break;

                    default:
                        if (c == delimiter)
                        {
                            fields.Add(current.ToString());
                            current.Clear();
                            rowHasData = true;
                        }
                        else
                        {
                            current.Append(c);
                            rowHasData = true;
                        }

                        justClosedQuote = false;
                        break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        if (rowHasData || fields.Count > 0 || current.Length > 0)
        {
            fields.Add(current.ToString());
            yield return new CsvRow(rowStartLine, fields.ToArray());
        }
    }
}
