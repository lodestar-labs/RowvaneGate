# Rowvane Gate

**Validate any data file — before it touches your systems.**

Gate is an API-first validation engine for the files organizations actually exchange:
deeply nested XML, JSON exports, and every flavor of flat file — including multi-record
CSV where the first field announces each line's record type, and fixed-width mainframe
layouts. One declarative **ruleset** describes the data's shape and the rules it must
satisfy; Gate streams the file through and returns a structured report with real line
numbers, raw values, and per-rule counts.

[![CI](https://github.com/KadjiProjects/RowvaneGate/actions/workflows/ci.yml/badge.svg)](https://github.com/KadjiProjects/RowvaneGate/actions/workflows/ci.yml)
[![License: BSL 1.1](https://img.shields.io/badge/license-BSL%201.1-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)

Gate stands alone, and it pairs naturally with **Rowvane**, the data-import platform:
validate at the gate, import what passes.

## Why Gate

- **One ruleset, every format.** Field rules (required, type, range, regex, lists,
  length), cross-field and cross-level comparisons (`haul.Weight ≤ parent.TotalWeight`),
  conditionals, composite uniqueness (file- or parent-scoped), parent-vs-children
  aggregates with deviation tolerances, row-count bounds — declared once in JSON,
  enforced identically for CSV, XML, and JSON representations of the same data.
- **Schema import.** Feed Gate an **XSD** or a **JSON Schema** and get a generated,
  editable ruleset: types, required fields, enumerations, patterns, and facets become
  native rules — which then validate *any* supported format, not just the schema's own.
  Native XSD pass-through validation (with the parser's real line numbers) is also built in.
- **Flat files, done properly.** Multi-record-type files with hierarchy reconstructed
  from record order; single-record CSV with header detection or positional mapping;
  fixed-width layouts; delimiter sniffing across comma/semicolon/tab/pipe; quoted fields,
  escaped quotes, embedded newlines (RFC 4180).
- **An analytical tier with an escape hatch.** Embedded DuckDB powers column profiling
  (`POST /api/profile`) and **SQL rules** — any dataset-level check you can express as a
  query over the staged file, each returned row becoming a finding. SQL rules run
  **sandboxed** by default: the file is materialized into a `data` table and DuckDB's
  filesystem access is disabled before the query runs, so a ruleset can never touch
  other files on the host.
- **Findings, not strings.** Every violation carries rule id, severity, entity, field,
  line, raw value, and message. Reports include per-rule totals (never truncated) and
  per-entity record counts. Streaming end to end: memory is bounded by one record
  subtree, not the file.

## Quick start

```bash
git clone https://github.com/KadjiProjects/RowvaneGate.git
cd RowvaneGate
dotnet run --project src/Rowvane.Gate.Api
```

Open http://localhost:5000/swagger (or the port shown). Register the sample ruleset and
validate the sample file:

```bash
curl -X PUT http://localhost:5000/api/rulesets/trips \
     --data-binary @samples/trips/trips.ruleset.json

curl -X POST http://localhost:5000/api/validate/trips \
     -F "file=@samples/trips/trips.csv"
```

The response is the full validation report. Or skip authoring entirely:

```bash
# XSD in → editable ruleset out (and registered)
curl -X POST "http://localhost:5000/api/rulesets/import/xsd?name=orders" -F "file=@orders.xsd"

# Column statistics for an unfamiliar file
curl -X POST http://localhost:5000/api/profile -F "file=@mystery.csv"
```

## A ruleset in 30 seconds

```json
{
  "name": "trips",
  "shape": {
    "name": "TR",
    "fields": [ { "name": "RecordType" }, { "name": "TripId" }, { "name": "TotalWeight" } ],
    "children": [ { "name": "HL", "required": true,
                    "fields": [ { "name": "RecordType" }, { "name": "HaulNo" }, { "name": "Weight" } ] } ]
  },
  "source": { "csv": { "mode": "multiRecord" } },
  "rules": [
    { "entity": "TR", "field": "TripId", "check": { "type": "required" } },
    { "entity": "TR", "check": { "type": "unique", "fields": [ "TripId" ] } },
    { "entity": "HL", "field": "Weight",
      "check": { "type": "compare", "op": "le", "otherField": "parent.TotalWeight" } },
    { "entity": "TR", "field": "TotalWeight",
      "check": { "type": "aggregate", "childEntity": "HL", "childField": "Weight",
                 "function": "sum", "deviationPercent": 5 } }
  ]
}
```

That single document validates the same trips whether they arrive as multi-record CSV,
nested XML, or a JSON array.

## Documentation

- [Validate &amp; import guide](docs/validate-and-import-guide.md) — the comprehensive
  walkthrough: validation alone, import alone with [Loadstone](https://github.com/KadjiProjects/Loadstone),
  or both chained into one pipeline.
- [Ruleset reference](docs/ruleset-reference.md) — every property of the ruleset
  document: shape, source options, all eleven check types, the report format, schema import.
- [Recipes](docs/recipes.md) — multi-record exchange files, XSD contracts over non-XML
  data, JSON exports, fixed-width layouts, SQL rules, CI gates.
- [Architecture overview](docs/architecture-overview.html) — the layered design, the
  streaming validation pass, rule compilation, and the analytics tier.
- [Developer guide](docs/developer-guide.html) — for engineers extending Gate: the record
  model, each reader, the engine's rule plan, extension seams, testing.

## Running in production

Gate is a single stateless-ish container: the only state is the ruleset directory and a
staging area for in-flight uploads.

- **Health probes** — `/health/live` (process up; never depends on anything external)
  and `/health/ready` (staging directory writable). Wire liveness and readiness to these
  respectively; plain `/health` combines everything.
- **Scaling out** — point `Gate__RulesetDirectory` at a shared volume (App Service
  `%HOME%\data`, a Kubernetes RWX volume): replicas read through to it, so a ruleset
  registered or updated on one replica is picked up by the others on their next lookup,
  no restart needed. Without a shared volume, run a single replica.
- **Authentication** — set `Gate__ApiKey` to require an `X-Api-Key` header on every
  `/api` request. Without it, run Gate only behind an authenticating gateway: ruleset
  registration is a privileged operation (SQL rules run queries on the host, sandboxed
  or not).
- **Upload limits** — Gate accepts up to `Gate__MaxUploadBytes` (default 2 GB), but your
  ingress usually caps far lower (nginx defaults to 1 MB). Raise
  `proxy-body-size`/`client_max_body_size` alongside it or uploads die at the proxy with
  an opaque 413.
- **Housekeeping** — staged uploads are deleted after validation and a background
  sweeper removes anything older than `Gate__StagedFileRetention` (default 1 h), so
  crashes can't slowly fill the disk.
- **Telemetry** — set `OTEL_EXPORTER_OTLP_ENDPOINT` and traces + metrics (files
  validated, records read, findings by severity, validation duration) flow to any OTLP
  backend: Azure Monitor, Grafana, Jaeger, Datadog.

## Project layout

| Project | What it is |
| --- | --- |
| `Rowvane.Gate.Core` | Ruleset model, record model, findings/report, contracts. No dependencies. |
| `Rowvane.Gate.Readers` | Streaming CSV (single / multi-record / fixed-width, sniffing), XML, JSON. |
| `Rowvane.Gate.Schemas` | XSD and JSON Schema importers; native XSD validation. |
| `Rowvane.Gate.Engine` | Compiled rule evaluation, hierarchy checks, accumulators, the report builder. |
| `Rowvane.Gate.Analytics` | Embedded DuckDB: SQL rules and column profiling. |
| `Rowvane.Gate.Api` | The HTTP host: rulesets, validation, import, profiling, Swagger. |

## Roadmap

- Excel and Parquet sources
- Referential rules against external databases and APIs
- HTML and JUnit report renderers (CI gates)
- A ruleset editor UI
- In-process step package for Rowvane pipelines

## License

Source-available under the [Business Source License 1.1](LICENSE): **free for self-hosted
production use at any scale.** Each release converts to Apache 2.0 after four years.
Offering Gate as a hosted service or embedding it in a product you sell requires a
[commercial license](COMMERCIAL-LICENSE.md).
