namespace Rowvane.Gate.Rulesets;

/// <summary>Per-format options describing how files carry the ruleset's shape.</summary>
public sealed class SourceOptions
{
    public CsvOptions Csv { get; set; } = new();

    public XmlOptions Xml { get; set; } = new();

    public JsonOptions Json { get; set; } = new();
}

public enum CsvMode
{
    /// <summary>One record type; columns map by header (or by shape order without one).</summary>
    Single,

    /// <summary>
    /// Each line's record type is announced by a discriminator column (classic bank /
    /// mainframe / RDBES exchange shape). Hierarchy is reconstructed from record order:
    /// a record attaches to the most recent record of its parent entity.
    /// </summary>
    MultiRecord,

    /// <summary>Fixed-width columns per record type, using each field's start/length.</summary>
    FixedWidth,
}

public sealed class CsvOptions
{
    public CsvMode Mode { get; set; } = CsvMode.Single;

    /// <summary>Null = auto-detect among comma, semicolon, tab, and pipe.</summary>
    public char? Delimiter { get; set; }

    /// <summary>Single mode only. Null = auto-detect (header row matches shape field names).</summary>
    public bool? HasHeaderRow { get; set; }

    /// <summary>MultiRecord/FixedWidth: 0-based column (or character position) of the record type.</summary>
    public int RecordTypeIndex { get; set; }

    /// <summary>FixedWidth: length of the record-type prefix, when fields don't cover it.</summary>
    public int RecordTypeLength { get; set; } = 2;

    /// <summary>Record-type values not described by the shape: fail the file or skip the line.</summary>
    public bool IgnoreUnknownRecordTypes { get; set; }
}

public sealed class XmlOptions
{
    /// <summary>Optional wrapper element; root-entity elements are matched only inside it.</summary>
    public string? RootElement { get; set; }
}

public sealed class JsonOptions
{
    /// <summary>Property holding the record array when the document is an object.</summary>
    public string? RootProperty { get; set; }
}
