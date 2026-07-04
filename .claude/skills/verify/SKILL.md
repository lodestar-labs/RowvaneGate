---
name: verify
description: Build, launch, and drive the Rowvane Gate API to verify changes end-to-end over HTTP.
---

# Verifying Rowvane Gate

## Build & launch

```powershell
dotnet build src/Rowvane.Gate.Api -v q          # ALWAYS rebuild the Api project first:
                                                # `dotnet test` only refreshes the test bin,
                                                # so `dotnet run --no-build` serves stale DLLs otherwise
dotnet run --project src/Rowvane.Gate.Api --no-build --urls http://localhost:5199   # run in background
```

Poll `GET /health` until it returns `Healthy` (a few seconds). Swagger UI at `/swagger`.

## Drive it

Register a ruleset, then validate files against it (multipart form, field name `file`):

```powershell
curl.exe -s -X PUT http://localhost:5199/api/rulesets/<name> --data-binary "@ruleset.json"
curl.exe -s -X POST "http://localhost:5199/api/validate/<name>" -F "file=@data.csv"
curl.exe -s -X POST "http://localhost:5199/api/rulesets/import/jsonschema?name=x&rootEntity=W" -F "file=@schema.json"
curl.exe -s -X POST "http://localhost:5199/api/rulesets/import/xsd?name=x" -F "file=@schema.xsd"
curl.exe -s -X POST "http://localhost:5199/api/validate/xsd" -F "xml=@doc.xml" -F "xsd=@schema.xsd"
curl.exe -s -X POST "http://localhost:5199/api/profile" -F "file=@data.csv"    # needs DuckDB, csv/json/parquet only
```

A compact multi-record test ruleset (Trip → Haul → Sample) lives in
`tests/Rowvane.Gate.Tests/TestData.cs`; mirror it as JSON when you need one over HTTP.

## Gotchas

- Format inference is by file extension (`csv/txt/dat/fw`, `xml`, `json`); override with `?format=`.
- The API persists registered rulesets to `data/rulesets` relative to its working
  directory (ends up under `src/Rowvane.Gate.Api/`); delete those leftovers after verifying.
- Structural file errors (bad record type, orphaned child records) return HTTP 400;
  rule violations return HTTP 200 with `valid: false` and findings.
- Kill the server with `Get-Process -Name "Rowvane.Gate.Api" | Stop-Process -Force`.
