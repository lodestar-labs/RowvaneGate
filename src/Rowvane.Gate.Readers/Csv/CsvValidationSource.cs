using System.Runtime.CompilerServices;
using System.Text;
using Rowvane.Gate.Records;
using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Sources;

namespace Rowvane.Gate.Readers.Csv;

/// <summary>
/// Flat-file source covering the shapes that occur commercially:
/// <para><b>Single</b> — one record type per file; columns map by header row or, without
/// one, positionally by the shape's field order.</para>
/// <para><b>MultiRecord</b> — every line announces its record type in a discriminator
/// column (bank files, mainframe extracts, the RDBES exchange format). The hierarchy is
/// reconstructed from record order: each record attaches to the most recent record of its
/// parent entity, and a completed root subtree is yielded as soon as the next root
/// begins, keeping memory bounded.</para>
/// <para><b>FixedWidth</b> — like MultiRecord, but fields are sliced by each field's
/// start/length instead of delimiters.</para>
/// Delimiter and header presence are sniffed when the ruleset doesn't pin them.
/// </summary>
public sealed class CsvValidationSource : IValidationSource
{
    public string Format => "csv";

    public IReadOnlyList<string> Extensions { get; } = ["csv", "txt", "dat", "fw"];

    public async IAsyncEnumerable<ValidationRecord> ReadAsync(
        Stream stream,
        RulesetDocument ruleset,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = ruleset.Source.Csv;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);

        var source = options.Mode switch
        {
            CsvMode.Single => ReadSingleAsync(reader, ruleset, cancellationToken),
            CsvMode.MultiRecord => ReadMultiRecordAsync(reader, ruleset, cancellationToken),
            CsvMode.FixedWidth => ReadFixedWidthAsync(reader, ruleset, cancellationToken),
            _ => throw new SourceFormatException($"Unsupported CSV mode '{options.Mode}'."),
        };

        await foreach (var record in source.WithCancellation(cancellationToken))
        {
            yield return record;
        }
    }

    // ---------------------------------------------------------------- Single

    private static async IAsyncEnumerable<ValidationRecord> ReadSingleAsync(
        StreamReader reader,
        RulesetDocument ruleset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var entity = ruleset.Shape;
        if (entity.Children.Count > 0)
        {
            throw new SourceFormatException(
                $"Ruleset '{ruleset.Name}' is hierarchical; use csv mode 'multiRecord' (or 'fixedWidth') for flat files.");
        }

        var (delimiter, buffered) = await ResolveDelimiterAsync(reader, ruleset, cancellationToken);
        string[]? header = null;
        var headerDecided = ruleset.Source.Csv.HasHeaderRow is not null;
        var hasHeader = ruleset.Source.Csv.HasHeaderRow ?? false;

        await foreach (var row in ParseBuffered(buffered, reader, delimiter, cancellationToken))
        {
            if (header is null)
            {
                if (!headerDecided)
                {
                    hasHeader = CsvSniffer.LooksLikeHeader(row.Fields, entity.Fields.Select(f => f.Name));
                    headerDecided = true;
                }

                if (hasHeader)
                {
                    header = [.. row.Fields.Select(f => f.Trim())];
                    continue;
                }

                header = [.. entity.Fields.Select(f => f.Name)];
            }

            yield return BuildRecord(entity, header, row.Fields, row.LineNumber, entity.Name);
        }
    }

    // ----------------------------------------------------------- MultiRecord

    private static async IAsyncEnumerable<ValidationRecord> ReadMultiRecordAsync(
        StreamReader reader,
        RulesetDocument ruleset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = ruleset.Source.Csv;
        var (delimiter, buffered) = await ResolveDelimiterAsync(reader, ruleset, cancellationToken);
        var assembler = new HierarchyAssembler(ruleset);

        await foreach (var row in ParseBuffered(buffered, reader, delimiter, cancellationToken))
        {
            if (row.Fields.Count <= options.RecordTypeIndex)
            {
                throw new SourceFormatException(
                    $"Line {row.LineNumber}: no record-type column at index {options.RecordTypeIndex}.");
            }

            var recordType = row.Fields[options.RecordTypeIndex].Trim();
            var entity = ruleset.FindEntity(recordType);
            if (entity is null)
            {
                if (options.IgnoreUnknownRecordTypes)
                {
                    continue;
                }

                throw new SourceFormatException(
                    $"Line {row.LineNumber}: unknown record type '{recordType}'. " +
                    "Add it to the ruleset shape or set source.csv.ignoreUnknownRecordTypes.");
            }

            var header = entity.Fields.Select(f => f.Name).ToArray();
            var record = BuildRecord(entity, header, row.Fields, row.LineNumber, recordType);
            var completedRoot = assembler.Place(record, row.LineNumber);
            if (completedRoot is not null)
            {
                yield return completedRoot;
            }
        }

        if (assembler.CurrentRoot is not null)
        {
            yield return assembler.CurrentRoot;
        }
    }

    // ------------------------------------------------------------ FixedWidth

    private static async IAsyncEnumerable<ValidationRecord> ReadFixedWidthAsync(
        StreamReader reader,
        RulesetDocument ruleset,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = ruleset.Source.Csv;
        var assembler = new HierarchyAssembler(ruleset);
        string? line;
        long lineNumber = 0;

        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            lineNumber++;
            if (line.Length == 0)
            {
                continue;
            }

            if (line.Length < options.RecordTypeIndex + options.RecordTypeLength)
            {
                throw new SourceFormatException($"Line {lineNumber}: shorter than the record-type prefix.");
            }

            var recordType = line.Substring(options.RecordTypeIndex, options.RecordTypeLength).Trim();
            var entity = ruleset.FindEntity(recordType);
            if (entity is null)
            {
                if (options.IgnoreUnknownRecordTypes)
                {
                    continue;
                }

                throw new SourceFormatException($"Line {lineNumber}: unknown record type '{recordType}'.");
            }

            var record = new ValidationRecord
            {
                Entity = entity,
                Location = new SourceLocation(lineNumber, recordType),
            };
            foreach (var field in entity.Fields)
            {
                if (field.Start is not { } start || field.Length is not { } length)
                {
                    throw new SourceFormatException(
                        $"Field '{field.Name}' on '{entity.Name}' has no start/length; fixed-width layouts require both.");
                }

                var from = start - 1;
                var value = from >= line.Length
                    ? null
                    : line.Substring(from, Math.Min(length, line.Length - from)).Trim();
                record.Fields[field.Name] = string.IsNullOrEmpty(value) ? null : value;
            }

            var completedRoot = assembler.Place(record, lineNumber);
            if (completedRoot is not null)
            {
                yield return completedRoot;
            }
        }

        if (assembler.CurrentRoot is not null)
        {
            yield return assembler.CurrentRoot;
        }
    }

    // -------------------------------------------------------------- Plumbing

    /// <summary>
    /// Rebuilds the hierarchy of a multi-record file from record order: each record
    /// attaches to the most recent record of its parent entity. Returns the previous root
    /// subtree whenever a new root begins (it is complete by definition of the format).
    /// </summary>
    private sealed class HierarchyAssembler
    {
        private readonly Dictionary<string, EntityShape?> _parentOf;
        private readonly Dictionary<string, string[]> _descendantsOf;
        private readonly Dictionary<string, ValidationRecord> _latest = new(StringComparer.OrdinalIgnoreCase);

        public HierarchyAssembler(RulesetDocument ruleset)
        {
            _parentOf = ruleset.EnumerateEntities()
                .ToDictionary(e => e.Name, ruleset.FindParent, StringComparer.OrdinalIgnoreCase);
            _descendantsOf = ruleset.EnumerateEntities()
                .ToDictionary(e => e.Name, Descendants, StringComparer.OrdinalIgnoreCase);

            static string[] Descendants(EntityShape entity)
            {
                var names = new List<string>();
                var queue = new Queue<EntityShape>(entity.Children);
                while (queue.Count > 0)
                {
                    var child = queue.Dequeue();
                    names.Add(child.Name);
                    foreach (var grandChild in child.Children)
                    {
                        queue.Enqueue(grandChild);
                    }
                }

                return [.. names];
            }
        }

        public ValidationRecord? CurrentRoot { get; private set; }

        public ValidationRecord? Place(ValidationRecord record, long lineNumber)
        {
            var parentShape = _parentOf[record.Entity.Name];
            ValidationRecord? completed = null;

            if (parentShape is null)
            {
                completed = CurrentRoot;
                CurrentRoot = record;

                // The previous tree is complete; its records must no longer act as
                // parents, or later records would silently attach to an already-yielded
                // (and already-validated) subtree.
                _latest.Clear();
            }
            else
            {
                if (!_latest.TryGetValue(parentShape.Name, out var parent))
                {
                    throw new SourceFormatException(
                        $"Line {lineNumber}: '{record.Entity.Name}' record appears before any '{parentShape.Name}' parent record.");
                }

                record.Parent = parent;
                parent.Children.Add(record);
            }

            _latest[record.Entity.Name] = record;

            // A new record closes its own subtree's context: without this, a record placed
            // after it (e.g. a detail under a *second* branch) would attach to a stale
            // deeper record left over from the previous sibling branch.
            foreach (var descendant in _descendantsOf[record.Entity.Name])
            {
                _latest.Remove(descendant);
            }

            return completed;
        }
    }

    private static ValidationRecord BuildRecord(
        EntityShape entity,
        string[] header,
        IReadOnlyList<string> cells,
        long lineNumber,
        string path)
    {
        var record = new ValidationRecord
        {
            Entity = entity,
            Location = new SourceLocation(lineNumber, path),
        };

        for (var i = 0; i < header.Length; i++)
        {
            var value = i < cells.Count ? cells[i] : null;
            record.Fields[header[i]] = string.IsNullOrEmpty(value) ? null : value;
        }

        return record;
    }

    /// <summary>Reads sample lines for sniffing, then replays them ahead of the live stream.</summary>
    private static async Task<(char Delimiter, List<string> BufferedLines)> ResolveDelimiterAsync(
        StreamReader reader,
        RulesetDocument ruleset,
        CancellationToken cancellationToken)
    {
        if (ruleset.Source.Csv.Delimiter is { } configured)
        {
            return (configured, []);
        }

        var sample = new List<string>();
        string? line;
        while (sample.Count < 20 && (line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            sample.Add(line);
        }

        return (CsvSniffer.DetectDelimiter(sample), sample);
    }

    private static async IAsyncEnumerable<CsvParser.CsvRow> ParseBuffered(
        List<string> bufferedLines,
        StreamReader reader,
        char delimiter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (bufferedLines.Count > 0)
        {
            // Replay the sniffing sample, then continue with the rest of the stream.
            // (A quoted field spanning the sample boundary would mis-split; pin the
            // delimiter in the ruleset for files that open with multi-line quotes.)
            using var replay = new StringReader(string.Join('\n', bufferedLines) + "\n");
            await foreach (var row in CsvParser.ParseAsync(replay, delimiter, cancellationToken))
            {
                yield return row;
            }

            await foreach (var row in CsvParser.ParseAsync(reader, delimiter, cancellationToken))
            {
                yield return new CsvParser.CsvRow(row.LineNumber + bufferedLines.Count, row.Fields);
            }

            yield break;
        }

        await foreach (var row in CsvParser.ParseAsync(reader, delimiter, cancellationToken))
        {
            yield return row;
        }
    }
}
