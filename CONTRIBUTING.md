# Contributing to Rowvane Gate

Thanks for taking the time — contributions of every size are welcome.

## Getting set up

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download).
2. `dotnet build` and `dotnet test` from the repository root should both be green before
   and after your change. CI treats warnings as errors.
3. `dotnet run --project src/Rowvane.Gate.Api` starts the API with Swagger; the
   `samples/trips` folder has a ruleset and file to play with.

## Making changes

- Open an issue first for anything beyond a small fix.
- Keep the layering intact: `Core` has no dependencies and defines the contracts; readers,
  schema importers, and analytics implement them. New formats and rule sources are new
  implementations, not edits to Core.
- Readers must stream — memory bounded by one record subtree, never the file.
- Findings are structured (rule id, severity, entity, field, line, raw value); never
  plain strings.
- Add or update tests for the behavior you change. The suite is plain NUnit and runs
  without external services (DuckDB is embedded).

## Licensing of contributions

Rowvane Gate uses the Business Source License 1.1 with a commercial tier (see
[COMMERCIAL-LICENSE.md](COMMERCIAL-LICENSE.md)). By submitting a contribution you agree it
may be distributed under the project's current license and under commercial licenses, and
that the project may be relicensed in the future. You keep the copyright to your work.
