# Ruleset reference

A ruleset is a JSON document that fully describes one validation: the **shape** of the
data (entities, fields, hierarchy), how each supported **format** carries that shape, and
the **rules** the data must satisfy. Rulesets are validated on registration; an invalid
ruleset is rejected with every problem listed, so authors fix them all in one pass.

Rulesets can be registered three ways, all equivalent:

- `PUT /api/rulesets/{name}` with the ruleset as the request body;
- a `.json` file in the ruleset directory (`Gate:RulesetDirectory`), loaded at startup;
- generated from a schema: `POST /api/rulesets/import/xsd` or
  `POST /api/rulesets/import/jsonschema` (see [Schema import](#schema-import)).

Property names are camelCase and case-insensitive. Enum values are camelCase strings.
Comments and trailing commas are tolerated in ruleset files.

## Top level

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | Unique ruleset name; also the route segment for validation. |
| `version` | string | `"1"` | Free-form version label. |
| `description` | string | – | Shown in the API. |
| `shape` | entity | required | The root entity of the hierarchy. |
| `source` | object | `{}` | Per-format options, see below. |
| `rules` | rule[] | `[]` | The rules, see below. |
| `maxFindingsPerRule` | int | `1000` | Findings kept per rule before the list is truncated. Per-rule **counts** are never truncated. |

## Shape: entities and fields

The shape is a tree of entities. An entity's `name` matches the XML element, the JSON
property, or the CSV record-type value (for multi-record files). Matching is
case-insensitive everywhere.

| Entity property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | Unique within the ruleset. |
| `fields` | field[] | required | At least one. |
| `children` | entity[] | `[]` | Nested entities, any depth. |
| `required` | bool | `false` | A parent record without at least one instance of this child is a finding (rule id `SHAPE`, severity error). |

| Field property | Type | Default | Description |
| --- | --- | --- | --- |
| `name` | string | required | Unique within the entity. |
| `start` | int | – | Fixed-width only: 1-based start column. |
| `length` | int | – | Fixed-width only: width in characters. |

Values are kept as **raw strings** exactly as the file carried them (a JSON number keeps
its literal text). Blank values become null; a missing value and a blank value are the
same thing to every check. Type conversion never happens implicitly — a `dataType` check
*verifies* parseability, it does not convert.

## Source options

### `source.csv`

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `mode` | enum | `single` | `single`, `multiRecord`, or `fixedWidth` (see below). |
| `delimiter` | string (1 char) | auto | Null = sniffed among comma, semicolon, tab, and pipe. |
| `hasHeaderRow` | bool | auto | Single mode only. Null = detected (first row matches shape field names). |
| `recordTypeIndex` | int | `0` | MultiRecord: 0-based column of the record type. FixedWidth: 0-based character position. |
| `recordTypeLength` | int | `2` | FixedWidth: length of the record-type prefix. |
| `ignoreUnknownRecordTypes` | bool | `false` | Skip lines whose record type isn't in the shape, instead of failing the file. |

**Modes:**

- **single** — one record type per file. Columns map by header row, or positionally by
  the shape's field order when there is no header.
- **multiRecord** — every line announces its record type in a discriminator column
  (bank files, mainframe extracts, the RDBES exchange format). The hierarchy is
  reconstructed from record order: each record attaches to the most recent record of its
  parent entity. A child record appearing where no parent exists in the current tree is a
  structural error. Fields map positionally, in the shape's declared field order —
  include the record-type column itself as the first field.
- **fixedWidth** — like multiRecord, but fields are sliced by each field's
  `start`/`length` instead of delimiters. Every field needs both. Values are trimmed.

Quoted fields, escaped quotes (`""`), and delimiters or line breaks inside quotes are
handled per RFC 4180. Findings report the physical line each record started on.

> **Sniffing caveat:** delimiter detection samples the first 20 lines and replays them.
> A quoted field spanning that boundary would mis-split — pin `delimiter` in the ruleset
> for files that open with multi-line quoted values.

### `source.xml`

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `rootElement` | string | – | Optional wrapper element; root-entity elements are matched only inside it. |

Elements matching child entities recurse; elements **or attributes** matching shape
fields become values; anything else is skipped, so documents may carry extra content
without breaking. Findings carry the parser's real line numbers. DTDs are prohibited.

### `source.json`

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `rootProperty` | string | – | Property holding the record array when the document is an object. |

A top-level **array** streams element by element with bounded memory — prefer it for
large exports. An **object** wrapping the array (via `rootProperty`, or a property named
after the root entity) is also accepted but parses the whole document. Nested arrays or
objects matching child entity names become children; numbers and booleans keep their
literal text; `null` is a missing value. Findings carry a JSONPath-style `path`
(`$[12].hauls[0]`) instead of line numbers.

## Rules

| Property | Type | Default | Description |
| --- | --- | --- | --- |
| `id` | string | auto | Stable identifier used in findings. Auto-assigned (`R001`, `R002`, …) when omitted. |
| `entity` | string | see note | Target entity. Required for all field-level checks; optional only for `rowCount`/`unique` (which name it anyway) and `sql`. |
| `field` | string | – | Target field, for field-level checks. |
| `severity` | enum | `error` | `info`, `warning`, or `error`. Only errors make the report invalid. |
| `message` | string | – | Overrides the check's generated message. |
| `when` | condition | – | The rule only applies when the condition holds, see below. |
| `check` | check | required | The check itself, discriminated by `"type"`. |

All field-level checks **pass on missing values** — presence is `required`'s job, and
only `required`'s. This keeps every other rule composable with optional fields.

### `when` — applicability condition

```json
"when": { "field": "Gear", "oneOf": [ "OTB", "PTM" ], "negate": false }
```

The rule applies only when the record's `field` value (trimmed, case-insensitive) is one
of `oneOf`. Set `negate: true` to invert.

## Checks

The `type` property selects the check. Eleven kinds:

### `required`

Value must be present and non-blank. No extra properties.

```json
{ "type": "required" }
```

### `dataType`

Value must parse as the given kind (invariant culture).

| Property | Default | Description |
| --- | --- | --- |
| `kind` | `string` | `string`, `integer`, `decimal`, `boolean`, `date`, `dateTime`, `time`, `guid`. |
| `format` | – | Exact format for date/time kinds, e.g. `"yyyyMMdd"`. Without it, invariant parsing. |

Booleans accept `true`/`false`/`1`/`0` (and upper/title-case variants).

```json
{ "type": "dataType", "kind": "date", "format": "yyyy-MM-dd" }
```

### `range`

Numeric bounds; the value must be numeric to be checked (a non-numeric value is its own
finding).

| Property | Default | Description |
| --- | --- | --- |
| `min`, `max` | – | At least one required. Inclusive unless marked exclusive. |
| `exclusiveMin`, `exclusiveMax` | `false` | Make the corresponding bound exclusive. |

```json
{ "type": "range", "min": 0, "exclusiveMin": true, "max": 100 }
```

### `length`

String length bounds: `min` and/or `max`.

```json
{ "type": "length", "max": 20 }
```

### `regex`

| Property | Default | Description |
| --- | --- | --- |
| `pattern` | required | .NET regular expression (2-second evaluation timeout). |
| `negate` | `false` | The value must **not** match. |

```json
{ "type": "regex", "pattern": "^T-\\d+$" }
```

### `inList`

| Property | Default | Description |
| --- | --- | --- |
| `values` | required | The allowed (or forbidden) values. |
| `caseInsensitive` | `true` | Match case-insensitively. |
| `negate` | `false` | The value must **not** be in the list. |

```json
{ "type": "inList", "values": [ "OTB", "PTM", "GNS" ] }
```

### `compare`

Compares the rule's field against a literal or another field — on the same record or an
ancestor.

| Property | Default | Description |
| --- | --- | --- |
| `op` | `eq` | `eq`, `ne`, `lt`, `le`, `gt`, `ge`. |
| `value` | – | Literal right-hand value. |
| `otherField` | – | Right-hand field path: `"OtherField"` or `"parent.Field"` — `parent.` may be repeated to climb further. |
| `numeric` | `true` | Compare numerically when **both** sides parse as numbers; otherwise ordinal string comparison. |

```json
{ "type": "compare", "op": "le", "otherField": "parent.TotalWeight" }
```

### `unique`

Composite-key uniqueness over one entity's records. Targets the rule's `entity`; no
`field` needed.

| Property | Default | Description |
| --- | --- | --- |
| `fields` | required | One or more fields forming the key. |
| `scope` | `dataset` | `dataset` (unique across the whole file) or `parent` (unique among siblings under the same parent record). |

Keys are case-insensitive; a record whose key fields are all missing is not checked.

```json
{ "type": "unique", "fields": [ "HaulNo" ], "scope": "parent" }
```

### `aggregate`

Aggregates a child entity's field per parent record and compares it against the parent's
own `field` — exactly (`op`) or within a percentage tolerance (`deviationPercent`).

| Property | Default | Description |
| --- | --- | --- |
| `childEntity` | required | Direct child entity to aggregate over. |
| `childField` | – | Child field to aggregate. Required unless `function` is `count`. |
| `function` | `sum` | `sum`, `count`, `avg`, `min`, `max`. |
| `op` | `eq` | Comparison when no deviation is given. |
| `deviationPercent` | – | When set, `op` is ignored and \|aggregate − parent\| ≤ pct of the parent value applies. |

```json
{ "type": "aggregate", "childEntity": "HL", "childField": "Weight",
  "function": "sum", "deviationPercent": 5 }
```

### `rowCount`

Bounds on how many records of the rule's `entity` the file may contain: `min` and/or
`max`. Evaluated once per file, after streaming completes.

```json
{ "type": "rowCount", "min": 1 }
```

### `sql`

The escape hatch: a SQL query executed by the embedded DuckDB engine over the staged
file. **Every returned row is a finding** — SELECT the columns you want in the message
(formatted as `name=value; name=value`). Requires the analytics package; supports csv,
json, and parquet staged files (not XML).

| Property | Description |
| --- | --- |
| `query` | The query. The staged file is exposed as the view `data`; the literal `{file}` is replaced with the quoted staged-file path. |

```json
{ "type": "sql",
  "query": "SELECT TripId, COUNT(*) AS n FROM data GROUP BY TripId HAVING COUNT(*) > 50" }
```

When analytics is unavailable (or no staged file exists), sql rules degrade to a
**warning** finding rather than failing the run. A query that errors produces an
**error** finding with the engine's message. At most 10 000 violation rows are read per
rule.

## The validation report

`POST /api/validate/{ruleset}` returns:

| Property | Description |
| --- | --- |
| `ruleset`, `source`, `format` | What was validated against what. |
| `startedAt`, `durationMs` | Timing. |
| `recordsRead` | Total records streamed. |
| `recordCounts` | Records per entity — structural sanity at a glance. |
| `errorCount`, `warningCount`, `infoCount` | Totals by severity, never truncated. |
| `findingsByRule` | Total findings per rule id, **including** those truncated from the list. |
| `findings` | The findings, truncated per rule at `maxFindingsPerRule`. |
| `valid` | `true` when no error-severity findings were produced. |

Each finding:

| Property | Description |
| --- | --- |
| `ruleId` | The rule that fired — or `SHAPE` (required-child violations) or `XSD` (native XSD validation). |
| `severity` | `info` / `warning` / `error`. |
| `entity`, `field` | Where in the model. |
| `line` | Physical line in the source, when the parser provides one (CSV, XML). |
| `path` | Logical location — record-type / element path / JSONPath. |
| `rawValue` | What the file actually contained. |
| `message` | Why it failed. |

Structural problems that prevent reading the file at all (malformed XML, an unknown
record type, a child record with no parent in the current tree) are **not** findings —
they fail the request with HTTP 400 and a message pointing at the offending line.

## Schema import

Both importers generate a *native, editable* ruleset — after import there is nothing
schema-specific left; edit and extend it like any hand-written ruleset. The generated
rules then validate **any** supported format of the same data, not just the schema's own.

**XSD** (`POST /api/rulesets/import/xsd?name=…&root=…&register=true`):

| XSD construct | Becomes |
| --- | --- |
| element hierarchy (complex types with child elements) | entity shape |
| simple-typed elements / attributes | fields |
| `minOccurs ≥ 1` / `use="required"` | `required` rule |
| base type (`xs:int`, `xs:decimal`, `xs:date`, …) | `dataType` rule |
| `xs:enumeration` facets | `inList` rule (case-sensitive) |
| `xs:pattern` facet | `regex` rule (anchored `^(?:…)$`) |
| `min/maxInclusive`, `min/maxExclusive` facets | `range` rule |
| `length`, `minLength`, `maxLength` facets | `length` rule |

`root` selects the global element when the schema declares more than one. Recursive
types are cut off at the first repetition.

**JSON Schema** (`POST /api/rulesets/import/jsonschema?name=…&rootEntity=…`), the widely
used subset: `type`, `properties`, `required`, `items`, `enum`, `pattern`,
`minimum`/`maximum` (numeric draft 6+ **and** boolean draft-4 exclusive forms),
`minLength`/`maxLength`, and `format` (`date`, `date-time`, `time`, `uuid` map to
`dataType` kinds). Object properties become fields; object- or array-of-object
properties become child entities. `["string","null"]` unions use the first non-null
type. `rootEntity` names the root (default `Record`), since JSON Schemas rarely name it.

**Native XSD pass-through** (`POST /api/validate/xsd`, form files `xml` + `xsd`) runs the
document through the .NET schema validator and returns every violation as a structured
finding with the parser's real line numbers — for consumers whose contract *is* the XSD.
