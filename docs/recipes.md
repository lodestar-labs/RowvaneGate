---
title: Recipes
---

# Recipes

Practical patterns for the situations Gate was built for. Each recipe is a complete
approach: the ruleset ideas, the request, and what to watch afterwards.

## 1. A multi-record exchange file (bank, mainframe, RDBES-style)

**Situation:** a supplier delivers flat files where every line starts with a record-type
code — `TR` for a trip, `HL` for a haul under it, `SA` for a sample under that — and the
hierarchy is implied by record order. Classic formats: payment files, mainframe extracts,
scientific data exchanges.

**Approach:**

- Model each record type as an entity, nested to match the real hierarchy, and set
  `"source": { "csv": { "mode": "multiRecord" } }`. Include the record-type column itself
  as the first field of every entity — fields map positionally.
- Mark children `"required": true` where a parent without them is meaningless (a trip
  with no hauls). Gate reports these as `SHAPE` findings with the parent's line number.
- Put a dataset-scoped `unique` rule on the natural key of the root (`TripId`) and
  parent-scoped `unique` rules on sequence numbers within a parent (`HaulNo` per trip) —
  the classic double-keying mistakes in hand-built files.
- Use `aggregate` to enforce the totals these formats love to carry:
  `sum(HL.Weight)` within 5 % of the trip's declared `TotalWeight`.
- Leave `delimiter` unset for well-behaved files (Gate sniffs comma/semicolon/tab/pipe);
  pin it for files with quoted multi-line values.

Structural violations — an unknown record type, a child line with no parent above it —
fail the upload with HTTP 400 and the offending line number, because a file whose
*skeleton* is broken can't be meaningfully rule-checked. Set
`ignoreUnknownRecordTypes: true` if the supplier legitimately sends record types you
don't care about.

```bash
curl -X POST http://localhost:5000/api/validate/trips -F "file=@delivery.csv"
```

The report's `recordCounts` is your first glance: if Tuesday's file suddenly has 4 trips
instead of 40, you know before reading a single finding.

## 2. The contract is an XSD — but the data isn't always XML

**Situation:** an industry standard defines the exchange format as an XML schema, and
partners are supposed to comply. Some send XML; others send "the same data" as CSV, and
you still want the XSD's constraints applied to it.

**Approach:**

- Import the schema once: `POST /api/rulesets/import/xsd?name=orders`. Types, required
  elements, enumerations, patterns, and value facets become native rules; the element
  hierarchy becomes the shape.
- The generated ruleset is a starting point, not a cage — open it (`GET
  /api/rulesets/orders`), add the cross-field rules XSD cannot express (`compare`,
  `unique`, `aggregate`), tune severities, and `PUT` it back.
- XML deliveries can *also* be validated against the schema natively —
  `POST /api/validate/xsd` with both files — which catches things the ruleset model
  doesn't (ordering, namespaces). Run both when the contract is strict.
- CSV or JSON deliveries of the same data validate against the imported ruleset with the
  identical rules — the thing XSD alone can never do.

```bash
curl -X POST "http://localhost:5000/api/rulesets/import/xsd?name=orders" -F "file=@orders.xsd"
curl -X POST http://localhost:5000/api/validate/orders -F "file=@partner-a.xml"
curl -X POST http://localhost:5000/api/validate/orders -F "file=@partner-b.csv"
```

## 3. A SaaS tool's JSON export

**Situation:** a nightly export from a CRM/ticketing/e-commerce API — a large JSON array
of objects with embedded arrays — feeds a downstream system that chokes on bad data days
later, where the error is expensive to trace back.

**Approach:**

- A top-level JSON array streams with bounded memory regardless of size. If the export
  wraps the array (`{ "items": [...] }`), set `"source": { "json": { "rootProperty": "items" } }`.
- Property matching is case-insensitive; embedded arrays and single objects matching
  child entity names become child records automatically.
- If the vendor publishes a JSON Schema, import it
  (`POST /api/rulesets/import/jsonschema?name=crm`) and refine; if not, upload a sample
  to `POST /api/profile` first — DuckDB's column statistics (types, min/max, null counts)
  tell you what rules the data actually needs.
- Findings carry JSONPath-style locations (`$[311].addresses[0]`), which is exactly what
  you paste into `jq` to inspect the offending element.

Gate validates the file *before* your importer touches it: schedule
`curl → validate → check .valid → import` and broken exports never enter the pipeline.

## 4. Fixed-width mainframe layouts

**Situation:** a legacy system emits fixed-width files — record type in the first two
characters, fields at documented column positions, spaces for padding.

**Approach:**

- Set `"mode": "fixedWidth"` and give every field its 1-based `start` and `length`
  straight from the layout document. Values are trimmed; all-space fields are missing.
- `recordTypeIndex`/`recordTypeLength` locate the discriminator (defaults: position 0,
  length 2).
- Hierarchy works exactly as in multi-record CSV — record order attaches children to the
  most recent parent.
- Lines shorter than a field simply yield a missing value (put `required` on the fields
  that must exist); lines shorter than the record-type prefix fail the file.

The layout document *is* the ruleset, transcribed once — instead of being re-implemented
in every consumer's substring arithmetic.

## 5. Dataset-level checks the rule model can't say: SQL rules

**Situation:** the check spans the whole file in a way no per-record rule can express —
"no vessel may appear on two trips in the same week", "at least 95 % of rows must have a
species code", statistical outlier detection.

**Approach:**

- Write it as a `sql` rule. The staged file is the DuckDB view `data`; every row the
  query returns becomes one finding, its columns formatted into the message:

```json
{ "id": "VESSEL-WEEK", "entity": "TR", "severity": "warning",
  "check": { "type": "sql",
    "query": "SELECT Vessel, week(DepartureDate::DATE) AS wk, COUNT(*) AS trips FROM data GROUP BY 1, 2 HAVING COUNT(*) > 1" } }
```

- SELECT only what you want operators to read — the columns are the message.
- SQL rules run after streaming, against the staged file (csv/json/parquet; XML is not
  supported by this tier). If analytics is disabled the rule degrades to a warning
  finding, so rulesets stay portable across deployments.
- Use `POST /api/profile` during authoring: `SUMMARIZE` output (per-column types, ranges,
  null percentages) is the fastest way to discover what a `sql` rule should assert.

## 6. Gate as a CI / pipeline gate

**Situation:** files land somewhere (SFTP drop, blob container, mailbox) and are imported
on a schedule. You want validation as an automatic, blocking step.

**Approach:**

- Keep rulesets in git and deploy them by `PUT` in CI — the ruleset is the interface
  contract with your data suppliers, version it like one.
- The validation call is one HTTP request; `valid` is the gate:

```bash
report=$(curl -sf -X POST "$GATE/api/validate/trips" -F "file=@$1")
echo "$report" | jq '{valid, errorCount, recordsRead, findingsByRule}'
[ "$(echo "$report" | jq -r .valid)" = "true" ] || { echo "$report" | jq '.findings[:20]'; exit 1; }
```

- Route the report back to the supplier on failure: `findings` carries line numbers, raw
  values, and reasons — it *is* the data-quality report, no reformatting needed.
- `findingsByRule` totals are never truncated, so `maxFindingsPerRule` can stay low
  (nobody reads 40 000 identical findings) while counts stay honest.

**Tip for all six:** severities are a triage tool. `error` blocks (`valid: false`),
`warning` informs, `info` annotates. Start strict rules as `warning`, watch a few real
deliveries, then promote to `error` once the supplier confirms — it beats bouncing the
first file of a new feed on a rule you guessed wrong.
