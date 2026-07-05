using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Rowvane.Gate.Engine;

/// <summary>
/// OpenTelemetry surface: one ActivitySource and one Meter, both named "Rowvane.Gate".
/// Hosts subscribe by adding the source/meter to their OTel pipeline; nothing is emitted
/// when no listener is attached.
/// </summary>
public static class GateDiagnostics
{
    public const string SourceName = "Rowvane.Gate";

    public static readonly ActivitySource ActivitySource = new(SourceName);

    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> FilesValidated =
        Meter.CreateCounter<long>("gate.files_validated", description: "Files run through validation");

    public static readonly Counter<long> RecordsRead =
        Meter.CreateCounter<long>("gate.records_read", description: "Records read across all validations");

    public static readonly Counter<long> FindingsRaised =
        Meter.CreateCounter<long>("gate.findings", description: "Findings raised, tagged by severity");

    public static readonly Histogram<double> ValidationDuration =
        Meter.CreateHistogram<double>("gate.validation.duration", unit: "s", description: "Wall time per validation");
}
