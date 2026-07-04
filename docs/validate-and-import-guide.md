# Validate and import: using Rowvane Gate and Loadstone — alone or together

[Rowvane Gate](https://github.com/KadjiProjects/RowvaneGate) and
[Loadstone](https://github.com/KadjiProjects/Loadstone) are two independent,
API-first tools built around the same idea: **describe your data once, declaratively,
and derive everything else.** Each stands alone; together they form a complete inbound
data pipeline: *validate at the gate, import what passes.*

| | Rowvane Gate | Loadstone |
| --- | --- | --- |
| **Job** | Validate a file and report every violation | Import a file into relational tables |
| **You author** | A **ruleset** (shape + rules) | A **manifest** (hierarchy + tables + keys + lookups) |
| **Input formats** | Multi-record CSV, fixed-width, single CSV, XML, JSON | XML, JSON, CSV (incl. zip-of-CSVs hierarchies) |
| **Output** | A structured validation report (findings with line numbers, raw values, per-rule counts) | Rows in your database, plus a job timeline and row-level rejection reports |
| **Storage needed** | None (rulesets are JSON files) | SQL Server / Azure SQL |
| **Execution model** | Synchronous: one request, one report | Asynchronous: durable job queue, retries, dead-lettering |
| **Answers the question** | *"Is this file acceptable?"* | *"Get this file into my tables, safely."* |

Use **Gate alone** when you need a quality gate, a CI check, or a supplier feedback
report — no database involved. Use **Loadstone alone** when your files are trusted (or
you're happy with row-level quarantine during import). Chain them when whole bad files
should be bounced *before* they consume an import job — with the report as the
supplier's fix-list.

---

## Part 1 — Data validation alone, with Rowvane Gate

### 1.1 Run it

```bash
git clone https://github.com/KadjiProjects/RowvaneGate.git
cd RowvaneGate
dotnet run --project src/Rowvane.Gate.Api
```

Swagger lives at `http://localhost:5000/swagger`. There is nothing else to set up — no
database, no broker. Two directories (configurable under the `Gate` section) hold state:
`data/rulesets` (registered rulesets as JSON) and `data/staging` (uploads during
validation).

### 1.2 Describe the data: author a ruleset

A ruleset declares the **shape** (entity hierarchy + fields), how each **format**
carries it, and the **rules**. Minimal example for hierarchical orders:

```json
{
  "name": "orders",
  "shape": {
    "name": "Order",
    "fields": [ { "name": "OrderNumber" }, { "name": "OrderDate" },
                { "name": "Country" },     { "name": "Total" } ],
    "children": [
      { "name": "Line", "required": true,
        "fields": [ { "name": "LineNumber" }, { "name": "Sku" }, { "name": "Quantity" } ] }
    ]
  },
  "source": { "xml": { "rootElement": "Orders" } },
  "rules": [
    { "entity": "Order", "field": "OrderNumber", "check": { "type": "required" } },
    { "entity": "Order", "field": "OrderNumber", "check": { "type": "length", "max": 20 } },
    { "entity": "Order", "field": "OrderDate",   "check": { "type": "dataType", "kind": "date" } },
    { "entity": "Order", "check": { "type": "unique", "fields": [ "OrderNumber" ] } },
    { "entity": "Line",  "field": "Quantity",
      "check": { "type": "range", "min": 1 } },
    { "entity": "Line",  "check": { "type": "unique", "fields": [ "LineNumber" ], "scope": "parent" } }
  ]
}
```

Eleven check types are available — `required`, `dataType`, `range`, `length`, `regex`,
`inList`, `compare` (cross-field, including `parent.` paths), `unique` (dataset- or
parent-scoped), `aggregate` (children vs a parent total), `rowCount`, and `sql`
(a DuckDB query over the whole file). All are conditionable with `when` and carry a
severity (`info` / `warning` / `error` — only errors make a file invalid). The full
specification is in the
[ruleset reference](https://github.com/KadjiProjects/RowvaneGate/blob/main/docs/ruleset-reference.md).

Three shortcuts when you don't want to start from a blank page:

```bash
# The contract already exists as a schema? Import it — the result is an editable native ruleset:
curl -X POST "http://localhost:5000/api/rulesets/import/xsd?name=orders"        -F "file=@orders.xsd"
curl -X POST "http://localhost:5000/api/rulesets/import/jsonschema?name=orders" -F "file=@orders.schema.json"

# Unfamiliar file? Profile it first — per-column types, ranges, and null counts tell you what rules it needs:
curl -X POST "http://localhost:5000/api/profile" -F "file=@mystery.csv"
```

### 1.3 Register and validate

```bash
curl -X PUT  http://localhost:5000/api/rulesets/orders --data-binary @orders.ruleset.json
curl -X POST http://localhost:5000/api/validate/orders -F "file=@delivery.xml"
```

Registration validates the ruleset itself and reports **every** structural problem at
once. Validation returns the full report synchronously:

```json
{
  "ruleset": "orders", "source": "delivery.xml", "format": "xml",
  "recordsRead": 412, "recordCounts": { "Order": 97, "Line": 315 },
  "errorCount": 2, "warningCount": 0,
  "findingsByRule": { "R003": 2 },
  "findings": [ { "ruleId": "R003", "severity": "error", "entity": "Order",
                  "field": "OrderDate", "line": 118, "rawValue": "01/06/2026",
                  "message": "Value is not a valid Date." } ],
  "valid": false
}
```

Key properties of the report:

- `valid` is simply "no error-severity findings" — your one-line gate condition.
- `findings` is capped per rule (default 1000) but `findingsByRule` and the severity
  counts are **never** truncated, so dashboards stay honest.
- The same ruleset validates the same data arriving as CSV, XML, or JSON — pass
  `?format=` to override extension-based detection.
- Structural failures (malformed XML, unknown record type, orphaned child record) are
  HTTP 400 with the offending line, not findings: a file whose skeleton is broken can't
  be meaningfully rule-checked.

### 1.4 Automate it

The validation call is one HTTP request, which makes Gate a natural CI step, SFTP-drop
hook, or pre-import filter:

```bash
report=$(curl -sf -X POST "$GATE/api/validate/orders" -F "file=@$1")
if [ "$(echo "$report" | jq -r .valid)" != "true" ]; then
  echo "$report" | jq '{errorCount, findingsByRule, findings: .findings[:20]}'
  exit 1
fi
```

Keep rulesets in git and `PUT` them during deployment — the ruleset *is* the interface
contract with your data suppliers. More patterns (multi-record bank files, fixed-width
layouts, SQL rules) are in the
[recipes](https://github.com/KadjiProjects/RowvaneGate/blob/main/docs/recipes.md).

---

## Part 2 — Data import alone, with Loadstone

### 2.1 Run it

The fastest route is Docker (SQL Server included):

```bash
git clone https://github.com/KadjiProjects/Loadstone.git
cd Loadstone
docker compose up --build
```

Open `http://localhost:8080` — the operations dashboard, with the sample **orders**
dataset registered and its tables created automatically (the raw API is at `/swagger`).
Without Docker: point `ConnectionStrings:Loadstone` at any SQL Server and
`dotnet run --project src/Loadstone.Api`.

### 2.2 Describe the dataset: author a manifest

A manifest declares the entity hierarchy **plus the relational mapping**: target tables,
key columns, natural keys for upserts, and reference-data lookups. The shipped sample:

```json
{
  "name": "orders",
  "root": {
    "name": "Order", "table": "Orders", "keyColumn": "OrderId",
    "naturalKey": [ "OrderNumber" ],
    "fields": [
      { "name": "OrderNumber", "required": true, "maxLength": 20 },
      { "name": "OrderDate", "type": "date" },
      { "name": "Country", "lookup": { "list": "countries", "onMissing": "autoCreate" } },
      { "name": "Total", "type": "decimal" }
    ],
    "children": [
      { "name": "Line", "table": "OrderLines", "keyColumn": "LineId",
        "parentKeyColumn": "OrderId", "naturalKey": [ "LineNumber" ],
        "fields": [
          { "name": "LineNumber", "type": "int32", "required": true },
          { "name": "Sku", "maxLength": 50 },
          { "name": "Quantity", "type": "int32" }
        ] }
    ]
  }
}
```

From this one document Loadstone derives parsing for all three formats, type conversion
and validation, staging DDL, the `MERGE` statements (orders upsert on `OrderNumber`,
lines on `OrderId + LineNumber`), foreign-key wiring, a durable queue named `orders`,
and the rejection reporting. Full specification: the
[manifest reference](https://github.com/KadjiProjects/Loadstone/blob/main/docs/manifest-reference.md).

### 2.3 Register, create tables, import

```bash
# Register (or drop the file into data/datasets and restart)
curl -X PUT http://localhost:8080/api/datasets/orders --data-binary @orders.dataset.json

# Let Loadstone create the target tables (or map onto existing ones)
curl http://localhost:8080/api/datasets/orders/schema          # inspect the DDL
curl -X POST http://localhost:8080/api/datasets/orders/schema/apply

# Import — returns 202 Accepted with a job id; the work happens on the durable queue
curl -X POST http://localhost:8080/api/datasets/orders/imports -F "file=@orders.xml"
```

The same dataset accepts JSON (`orders.json`) and hierarchical CSV (zip of
`Order.csv` + `Line.csv` linked by `_key`/`_parentKey` columns). Same manifest, same
tables, three formats.

### 2.4 Watch the job, read the rejections

```bash
curl http://localhost:8080/api/imports/<jobId>              # state, counts, attempts
curl http://localhost:8080/api/imports/<jobId>/events       # the stage timeline
curl http://localhost:8080/api/imports/<jobId>/rejections   # row-level failures
```

Loadstone's safety model is **row-level fault isolation**: a record that fails type
conversion, a required check, or reference-data resolution is diverted to the rejection
store — with entity, source line, field, raw value, and reason — and the rest of the
file imports. Jobs are at-least-once (retries with backoff, dead-lettering when
exhausted), and natural-key upserts make re-running a corrected file idempotent.

### 2.5 Reference data

Code fields resolve through pluggable lookups: built-in code-list tables (managed via
`/api/codelists`), configuration-defined SQL queries against any existing database
(`Loadstone:SqlLookups` — no code), or a custom `ILookupProvider` class. Per-field
policies decide what an unknown code means: `rejectRecord`, `rejectFile`, `useDefault`,
or `autoCreate`.

---

## Part 3 — Validate, then import: the combined pipeline

### 3.1 Why both, when Loadstone already validates?

The two validations have different jobs, and the difference is *scope*:

| Concern | Gate (before the queue) | Loadstone (during import) |
| --- | --- | --- |
| Types, required fields, lengths | ✔ as findings, whole file at once | ✔ as row rejections |
| Cross-field / cross-level rules (`Line.Quantity ≤ parent.Total`…) | ✔ (`compare`, `when`) | — |
| Duplicate keys **inside the file** | ✔ (`unique`, dataset- or parent-scoped) | last write wins via upsert |
| Totals reconciliation (sum of lines vs header total) | ✔ (`aggregate`, exact or tolerance) | — |
| File-level sanity (row counts, record-type structure) | ✔ (`rowCount`, shape, structural 400s) | — |
| Dataset-level analytics (ratios, outliers) | ✔ (`sql` over DuckDB) | — |
| Reference codes against **your master data** | — | ✔ (lookups + policies) |
| Actually landing rows, FK wiring, idempotent re-runs | — | ✔ |

So: **Gate decides whether the file deserves an import job at all**, and produces the
report you send back to the supplier. **Loadstone guarantees that whatever enters the
import is landed safely**, quarantining the stragglers its lookups and conversions
catch (Gate can't check your code lists; Loadstone can't compare a haul to its parent
trip). One bounces bad *files*; the other quarantines bad *rows*.

A practical bonus: a rejected file never consumes queue attempts, table locks, or
staging I/O — and the supplier gets feedback in seconds instead of after the nightly
import ran.

### 3.2 Keep the two documents in sync

The ruleset and the manifest describe the *same shape* from two angles. Author them
together — same entity names, same field names — and version them side by side in git.
The concepts map almost one-to-one:

| Concept | Gate ruleset | Loadstone manifest |
| --- | --- | --- |
| Hierarchy | `shape` (entities + `children`) | `root` (entities + `children`) |
| Must be present | rule `{ "check": { "type": "required" } }` | field `"required": true` |
| Data type | rule `dataType` (`kind`, `format`) | field `"type"` / `"format"` |
| Max length | rule `length` | field `"maxLength"` |
| Row identity | rule `unique` (reports duplicates) | `naturalKey` (upserts on it) |
| Parent must have child | entity `"required": true` (shape) | entity `"required": true` |
| Valid codes | rule `inList` (static) | `lookup` against live reference data |
| Format carriage | `source.csv/xml/json` | `source.csv/json` |

Everything below the line in §3.1's table (comparisons, aggregates, row counts, SQL
checks) exists only on the Gate side — that's the point of putting it in front.

For the sample **orders** dataset, the ruleset in §1.2 *is* the Gate-side twin of the
manifest in §2.2: same `Order`/`Line` hierarchy, `required`+`length` mirroring
`OrderNumber`'s constraints, `dataType` mirroring `OrderDate`, `unique` rules mirroring
both natural keys — plus the checks only Gate can do (`Line` required per order,
`Quantity ≥ 1`). If the contract exists as an XSD or JSON Schema, generate the ruleset
from it (`/api/rulesets/import/xsd`) and you only maintain the manifest by hand.

### 3.3 The pipeline

Both services are single processes; run them side by side (Gate on 5000, Loadstone on
8080 in the examples). The orchestration is ~15 lines of anything that can speak HTTP —
cron + bash, an Azure Function on a blob trigger, Power Automate, your scheduler:

```bash
#!/usr/bin/env bash
# ingest.sh <file> — validate with Gate, import with Loadstone if clean
set -euo pipefail
GATE=${GATE:-http://localhost:5000}
LOADSTONE=${LOADSTONE:-http://localhost:8080}
DATASET=orders
FILE=$1

# 1. Validate. Gate answers synchronously.
report=$(curl -sf -X POST "$GATE/api/validate/$DATASET" -F "file=@$FILE")

if [ "$(echo "$report" | jq -r .valid)" != "true" ]; then
  # 2a. Bounce the file. The report IS the supplier's fix-list:
  #     line numbers, raw values, reasons, per-rule counts.
  echo "$report" | jq '{source, errorCount, findingsByRule, findings: .findings[:50]}' \
    > "rejected/$(basename "$FILE").report.json"
  exit 1
fi

# 2b. Clean → hand it to the import queue. 202 Accepted, durable from here on.
job=$(curl -sf -X POST "$LOADSTONE/api/datasets/$DATASET/imports" -F "file=@$FILE")
jobId=$(echo "$job" | jq -r .jobId)
echo "queued import $jobId"

# 3. (Optional) wait for a terminal status and surface row-level rejections.
#    Failed = attempt failed, will retry; terminal states are below.
until status=$(curl -sf "$LOADSTONE/api/imports/$jobId" | jq -r .status); \
      [ "$status" = "Succeeded" ] || [ "$status" = "CompletedWithRejections" ] || [ "$status" = "DeadLettered" ]; do
  sleep 5
done
echo "import $status  ($(curl -sf "$LOADSTONE/api/imports/$jobId" | jq -r '"\(.rowsInserted) inserted, \(.rowsUpdated) updated, \(.recordsRejected) rejected"'))"
curl -sf "$LOADSTONE/api/imports/$jobId/rejections" | jq '.[:20]'
```

Behavior of the pair, end to end:

| The file is… | Gate says | Loadstone sees | Outcome |
| --- | --- | --- | --- |
| Clean | `valid: true` | clean records | rows landed; job `Succeeded` |
| Structurally broken (bad XML, orphan records) | HTTP 400 + line | — | bounced in seconds, nothing queued |
| Rule violations (bad dates, broken totals, duplicates) | `valid: false` + findings | — | bounced with the full forensic report |
| Clean per rules, but has unknown reference codes | `valid: true` | lookup misses | job `CompletedWithRejections`; those rows quarantined per the field's `onMissing` policy |

Severity is the tuning knob for the gray zone: Gate `warning`s don't block (`valid`
stays true), so you can watch a new rule against real deliveries before promoting it to
`error` — the supplier keeps shipping while you calibrate.

### 3.4 Operating the pair

- **Deploy** both as containers or App Services; they share nothing at runtime — the
  pipe between them is your orchestration script. Gate needs disk for staging; Loadstone
  needs its SQL database.
- **Version** the ruleset and the manifest together — one folder per feed in git
  (`feeds/orders/orders.ruleset.json` + `orders.dataset.json`), `PUT` both in CI. A
  schema change to the feed is one reviewable commit touching both documents.
- **Evolve** safely: add the new field to both documents; new Gate rules start as
  `warning`; promote to `error` once suppliers comply; only then rely on it downstream.
- **Debug** with each tool's own forensics: Gate's `findings` (with `Gate:KeepStagedFiles`
  to retain the staged file for SQL-rule debugging), Loadstone's job `events` timeline
  and `rejections` — plus `POST /api/profile` on Gate whenever a supplier's file "looks
  wrong" and you want column statistics before deciding whose bug it is.

---

## Endpoint quick reference

| Rowvane Gate (validation) | Loadstone (import) |
| --- | --- |
| `GET/PUT/DELETE /api/rulesets/{name}` | `GET/PUT/DELETE /api/datasets/{name}` |
| `GET /api/rulesets` | `GET /api/datasets` |
| `POST /api/rulesets/import/xsd` · `…/import/jsonschema` | `GET /api/datasets/{name}/schema` · `POST …/schema/apply` |
| `POST /api/validate/{ruleset}` → report (sync) | `POST /api/datasets/{name}/imports` → job (202, async) |
| `POST /api/validate/xsd` (native XSD pass-through) | `GET /api/imports/{id}` · `…/events` · `…/rejections` |
| `POST /api/profile` (DuckDB column statistics) | `GET/PUT /api/codelists/{list}` |
| `GET /health` · `/swagger` | `GET /health` · `/swagger` · dashboard at `/` |

Further reading: Gate's
[ruleset reference](https://github.com/KadjiProjects/RowvaneGate/blob/main/docs/ruleset-reference.md)
and [recipes](https://github.com/KadjiProjects/RowvaneGate/blob/main/docs/recipes.md);
Loadstone's
[manifest reference](https://github.com/KadjiProjects/Loadstone/blob/main/docs/manifest-reference.md)
and [recipes](https://github.com/KadjiProjects/Loadstone/blob/main/docs/recipes.md).
