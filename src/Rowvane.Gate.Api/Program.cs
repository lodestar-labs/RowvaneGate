using Microsoft.Extensions.Options;
using Rowvane.Gate.Analytics;
using Rowvane.Gate.Api;
using Rowvane.Gate.Engine;
using Rowvane.Gate.Findings;
using Rowvane.Gate.Readers.Csv;
using Rowvane.Gate.Readers.Json;
using Rowvane.Gate.Readers.Xml;
using Rowvane.Gate.Rulesets;
using Rowvane.Gate.Schemas.Json;
using Rowvane.Gate.Schemas.Xsd;
using Rowvane.Gate.Sources;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.Configure<GateOptions>(builder.Configuration.GetSection(GateOptions.SectionName));
    var maxUpload = builder.Configuration.GetSection(GateOptions.SectionName).Get<GateOptions>()?.MaxUploadBytes
        ?? new GateOptions().MaxUploadBytes;
    builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = maxUpload);
    builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
        options.MultipartBodyLengthLimit = maxUpload);

    builder.Services.AddSingleton<IValidationSource, CsvValidationSource>();
    builder.Services.AddSingleton<IValidationSource, XmlValidationSource>();
    builder.Services.AddSingleton<IValidationSource, JsonValidationSource>();
    builder.Services.AddSingleton<IAnalyticsRunner, DuckDbAnalytics>();
    builder.Services.AddSingleton<DuckDbAnalytics>();
    builder.Services.AddSingleton<ValidationEngine>();
    builder.Services.AddSingleton<IRulesetRegistry, RulesetRegistry>();
    builder.Services.AddSingleton(provider => new RulesetDirectoryStore(
        provider.GetRequiredService<IOptions<GateOptions>>().Value.RulesetDirectory,
        provider.GetRequiredService<ILogger<RulesetDirectoryStore>>()));
    builder.Services.AddHostedService<GateInitializer>();

    builder.Services.AddHealthChecks();
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen();

    var app = builder.Build();
    app.UseSerilogRequestLogging();
    app.UseSwagger();
    app.UseSwaggerUI(ui => ui.DocumentTitle = "Rowvane Gate API");

    MapRulesetEndpoints(app);
    MapValidationEndpoints(app);
    app.MapHealthChecks("/health");
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Rowvane Gate failed to start");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

static void MapRulesetEndpoints(WebApplication app)
{
    var group = app.MapGroup("/api/rulesets").WithTags("Rulesets");

    group.MapGet("/", (IRulesetRegistry registry) =>
        Results.Ok(registry.All.Select(r => new
        {
            r.Name,
            r.Version,
            r.Description,
            Entities = r.EnumerateEntities().Select(e => e.Name).ToArray(),
            RuleCount = r.Rules.Count,
        })));

    group.MapGet("/{name}", (string name, IRulesetRegistry registry) =>
        registry.Find(name) is { } ruleset
            ? Results.Text(RulesetSerializer.Serialize(ruleset), "application/json")
            : Results.NotFound());

    group.MapPut("/{name}", async (
        string name,
        HttpRequest request,
        IRulesetRegistry registry,
        RulesetDirectoryStore store,
        CancellationToken cancellationToken) =>
    {
        RulesetDocument ruleset;
        try
        {
            ruleset = await RulesetSerializer.DeserializeAsync(request.Body, cancellationToken);
        }
        catch (RulesetException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (!string.Equals(ruleset.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest(new { error = $"Ruleset name '{ruleset.Name}' does not match the route ('{name}')." });
        }

        try
        {
            await store.SaveAsync(ruleset, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Results.Problem($"The ruleset could not be persisted: {ex.Message}", statusCode: 500);
        }

        registry.Register(ruleset);
        return Results.Ok(new { ruleset.Name, rules = ruleset.Rules.Count });
    });

    group.MapDelete("/{name}", async (
        string name,
        IRulesetRegistry registry,
        RulesetDirectoryStore store,
        CancellationToken cancellationToken) =>
    {
        if (!registry.Remove(name))
        {
            return Results.NotFound();
        }

        await store.DeleteAsync(name, cancellationToken);
        return Results.NoContent();
    });

    group.MapPost("/import/xsd", async (
            IFormFile file,
            string name,
            string? root,
            bool? register,
            IRulesetRegistry registry,
            RulesetDirectoryStore store,
            CancellationToken cancellationToken) =>
        {
            RulesetDocument ruleset;
            try
            {
                await using var stream = file.OpenReadStream();
                ruleset = XsdImporter.Import(stream, name, root);
            }
            catch (RulesetException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (register ?? true)
            {
                await store.SaveAsync(ruleset, cancellationToken);
                registry.Register(ruleset);
            }

            return Results.Text(RulesetSerializer.Serialize(ruleset), "application/json");
        })
        .WithTags("Rulesets")
        .DisableAntiforgery();

    group.MapPost("/import/jsonschema", async (
            IFormFile file,
            string name,
            string? rootEntity,
            bool? register,
            IRulesetRegistry registry,
            RulesetDirectoryStore store,
            CancellationToken cancellationToken) =>
        {
            RulesetDocument ruleset;
            try
            {
                using var reader = new StreamReader(file.OpenReadStream());
                var json = await reader.ReadToEndAsync(cancellationToken);
                ruleset = JsonSchemaImporter.Import(json, name, rootEntity ?? "Record");
            }
            catch (RulesetException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }

            if (register ?? true)
            {
                await store.SaveAsync(ruleset, cancellationToken);
                registry.Register(ruleset);
            }

            return Results.Text(RulesetSerializer.Serialize(ruleset), "application/json");
        })
        .WithTags("Rulesets")
        .DisableAntiforgery();
}

static void MapValidationEndpoints(WebApplication app)
{
    app.MapPost("/api/validate/{ruleset}", async (
            string ruleset,
            IFormFile file,
            string? format,
            IRulesetRegistry registry,
            ValidationEngine engine,
            IEnumerable<IValidationSource> sources,
            IOptions<GateOptions> options,
            CancellationToken cancellationToken) =>
        {
            if (registry.Find(ruleset) is not { } document)
            {
                return Results.NotFound(new { error = $"Ruleset '{ruleset}' is not registered." });
            }

            var resolvedFormat = format ?? InferFormat(sources, file.FileName);
            if (resolvedFormat is null)
            {
                return Results.BadRequest(new
                {
                    error = $"Could not infer the format of '{file.FileName}'. Pass ?format=csv|xml|json explicitly.",
                });
            }

            // Stage to disk: validation streams from here, and sql rules need a real path.
            Directory.CreateDirectory(options.Value.StagingPath);
            var stagedPath = Path.Combine(
                options.Value.StagingPath,
                $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():n}{Path.GetExtension(file.FileName)}");
            await using (var staged = File.Create(stagedPath))
            {
                await file.OpenReadStream().CopyToAsync(staged, cancellationToken);
            }

            try
            {
                await using var stream = File.OpenRead(stagedPath);
                var report = await engine.ValidateAsync(
                    stream, resolvedFormat, document, file.FileName, stagedPath, cancellationToken);
                return Results.Ok(report);
            }
            catch (SourceFormatException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                if (!options.Value.KeepStagedFiles)
                {
                    File.Delete(stagedPath);
                }
            }
        })
        .WithTags("Validation")
        .DisableAntiforgery();

    app.MapPost("/api/validate/xsd", async (
            IFormFile xml,
            IFormFile xsd,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await using var xmlStream = xml.OpenReadStream();
                await using var xsdStream = xsd.OpenReadStream();
                var findings = await XsdValidator.ValidateAsync(xmlStream, xsdStream, cancellationToken: cancellationToken);
                return Results.Ok(new
                {
                    source = xml.FileName,
                    valid = findings.All(f => f.Severity != Severity.Error),
                    errorCount = findings.Count(f => f.Severity == Severity.Error),
                    warningCount = findings.Count(f => f.Severity == Severity.Warning),
                    findings,
                });
            }
            catch (RulesetException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        })
        .WithTags("Validation")
        .DisableAntiforgery();

    app.MapPost("/api/profile", async (
            IFormFile file,
            string? format,
            DuckDbAnalytics analytics,
            IOptions<GateOptions> options,
            CancellationToken cancellationToken) =>
        {
            var resolvedFormat = format ?? Path.GetExtension(file.FileName).TrimStart('.').ToLowerInvariant();
            Directory.CreateDirectory(options.Value.StagingPath);
            var stagedPath = Path.Combine(
                options.Value.StagingPath,
                $"profile_{Guid.NewGuid():n}{Path.GetExtension(file.FileName)}");
            await using (var staged = File.Create(stagedPath))
            {
                await file.OpenReadStream().CopyToAsync(staged, cancellationToken);
            }

            try
            {
                var profile = await analytics.ProfileAsync(stagedPath, resolvedFormat, cancellationToken);
                return Results.Ok(new { source = file.FileName, columns = profile });
            }
            catch (NotSupportedException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            finally
            {
                File.Delete(stagedPath);
            }
        })
        .WithTags("Profiling")
        .DisableAntiforgery();
}

static string? InferFormat(IEnumerable<IValidationSource> sources, string fileName)
{
    var extension = Path.GetExtension(fileName).TrimStart('.');
    if (extension.Length == 0)
    {
        return null;
    }

    return sources
        .FirstOrDefault(s => s.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        ?.Format;
}
