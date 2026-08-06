using System.Diagnostics;
using SchemaScope.Core.Comparison;
using SchemaScope.Core.Model;
using SchemaScope.SqlServer;

// ---------------------------------------------------------------------------
//  Phase 0 spike.
//
//  Answers the only two questions that can kill the project:
//    1. Is the catalog-first read actually fast?
//    2. Does the normalizer lie - does it report differences that are not real?
//
//  Usage:
//    schemascope-spike --source "<connection string>" --target "<cs>" [--target "<cs>"] ...
// ---------------------------------------------------------------------------

var argv = args;
if (argv.Length == 0 || argv.Contains("--help") || argv.Contains("-h"))
{
    PrintUsage();
    return 0;
}

string? sourceCs = null;
var targetCs = new List<string>();
string? sourceLabel = null;
var targetLabels = new List<string>();
string? jsonOut = null;
var showSamples = 5;

for (var i = 0; i < argv.Length; i++)
{
    switch (argv[i])
    {
        case "--source" or "-s" when i + 1 < argv.Length: sourceCs = argv[++i]; break;
        case "--target" or "-t" when i + 1 < argv.Length: targetCs.Add(argv[++i]); break;
        case "--label-source" when i + 1 < argv.Length: sourceLabel = argv[++i]; break;
        case "--label-target" when i + 1 < argv.Length: targetLabels.Add(argv[++i]); break;
        case "--json" when i + 1 < argv.Length: jsonOut = argv[++i]; break;
        case "--samples" when i + 1 < argv.Length: int.TryParse(argv[++i], out showSamples); break;
    }
}

if (string.IsNullOrWhiteSpace(sourceCs))
{
    Console.Error.WriteLine("error: --source is required.");
    PrintUsage();
    return 2;
}

var options = new CompareOptions();
var reader = new SqlServerSchemaReader();
var runner = new CompareRunner(reader);

var source = MakeSource(sourceCs, sourceLabel, 0);
var targets = targetCs
    .Select((cs, i) => MakeSource(cs, i < targetLabels.Count ? targetLabels[i] : null, i + 1))
    .ToList();

Console.WriteLine();
Console.WriteLine("SchemaScope spike");
Console.WriteLine(new string('=', 78));
Console.WriteLine($"  source   {source.DisplayName}");
foreach (var t in targets) Console.WriteLine($"  target   {t.DisplayName}");
Console.WriteLine();

var progress = new Progress<ReadProgress>(p =>
{
    if (p.Stage is "Done" or "Failed")
        Console.WriteLine($"  [{p.Stage,-6}] {p.SourceId,-10} {p.Detail}");
});

var sw = Stopwatch.StartNew();
CompareRun run;
try
{
    run = await runner.RunAsync(source, targets, options, progress);
}
catch (SchemaReadException ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"FAILED: {ex.Message}");
    if (!string.IsNullOrEmpty(ex.Hint)) Console.Error.WriteLine($"  hint: {ex.Hint}");
    return 1;
}
sw.Stop();

var result = run.Result;

// ---- timings -------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Timing");
Console.WriteLine(new string('-', 78));
Row("read all databases (parallel)", result.Timings.GetValueOrDefault("read"));
Row("build index", result.Timings.GetValueOrDefault("index"));
Row("compare + classify", result.Timings.GetValueOrDefault("compare"));
Row("summarise", result.Timings.GetValueOrDefault("summarise"));
Console.WriteLine(new string('-', 78));
Row("TOTAL", sw.ElapsedMilliseconds);

Console.WriteLine();
Console.WriteLine($"source: {run.Source.Objects.Count} objects  " +
                  $"(connect {run.Source.TimingsMs.GetValueOrDefault("connect")} ms, " +
                  $"catalog {run.Source.TimingsMs.GetValueOrDefault("catalog")} ms, " +
                  $"model {run.Source.TimingsMs.GetValueOrDefault("assemble")} ms, " +
                  $"hash {run.Source.TimingsMs.GetValueOrDefault("hash")} ms)");
Console.WriteLine($"        {run.Source.ProductVersion} / {run.Source.Edition}");

// ---- matrix --------------------------------------------------------------
Console.WriteLine();
Console.WriteLine("Result");
Console.WriteLine(new string('-', 78));
Console.WriteLine($"{"target",-22}{"same",7}{"fmt",7}{"diff",7}{"missing",9}{"extra",7}{"n/a",6}{"match",9}");
foreach (var t in result.Targets)
{
    if (t.Failed)
    {
        Console.WriteLine($"{Trim(t.Label, 22),-22}  FAILED: {t.Error}");
        continue;
    }
    Console.WriteLine(
        $"{Trim(t.Label, 22),-22}{t.Same,7}{t.FormattingOnly,7}{t.Different,7}" +
        $"{t.MissingInTarget,9}{t.OnlyInTarget,7}{t.Unavailable,6}{t.MatchPercent,8:0.0}%");
}

Console.WriteLine();
Console.WriteLine($"rows: {result.TotalRows}   with differences: {result.RowsWithDifferences}   " +
                  $"drifted in exactly one target: {result.SingleTargetDrift}");

// ---- accuracy check ------------------------------------------------------
// Anything reported as "formatting only" is a case the exact hash called a
// difference but the object is really the same. Each one is a normalizer rule
// worth reviewing before we trust the tool.
var formattingRows = result.Rows
    .Where(r => r.Cells.Any(c => c.Status == ObjectStatus.FormattingOnly))
    .ToList();

Console.WriteLine();
if (formattingRows.Count == 0)
{
    Console.WriteLine("Normalizer: no formatting-only differences. The exact hash agreed everywhere.");
}
else
{
    Console.WriteLine($"Normalizer: {formattingRows.Count} object(s) differ ONLY by formatting.");
    Console.WriteLine("A naive text compare would report every one of these as changed:");
    foreach (var r in formattingRows.Take(showSamples))
    {
        var note = r.Cells.FirstOrDefault(c => c.Status == ObjectStatus.FormattingOnly)?.Note;
        Console.WriteLine($"   {r.TypeLabel,-16} {Trim(r.FullName, 46),-46} {note}");
    }
    if (formattingRows.Count > showSamples)
        Console.WriteLine($"   ... and {formattingRows.Count - showSamples} more");
}

var unavailable = result.Rows.Count(r => r.Cells.Any(c => c.Status == ObjectStatus.Unavailable));
if (unavailable > 0)
    Console.WriteLine($"\n{unavailable} object(s) could not be read on at least one side.");

foreach (var w in result.Warnings) Console.WriteLine($"\nwarning: {w}");

// ---- biggest changes -----------------------------------------------------
var biggest = result.Rows
    .Where(r => r.Worst == ObjectStatus.Different)
    .OrderByDescending(r => r.Cells.Max(c => c.ChangedLines))
    .Take(showSamples)
    .ToList();

if (biggest.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("Biggest real changes");
    Console.WriteLine(new string('-', 78));
    foreach (var r in biggest)
    {
        var worstCell = r.Cells.OrderByDescending(c => c.ChangedLines).First();
        Console.WriteLine($"   {r.TypeLabel,-16} {Trim(r.FullName, 46),-46} " +
                          $"+{worstCell.AddedLines} -{worstCell.RemovedLines}");
    }
}

if (!string.IsNullOrWhiteSpace(jsonOut))
{
    var json = System.Text.Json.JsonSerializer.Serialize(result,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(jsonOut, json);
    Console.WriteLine($"\nwrote {jsonOut}");
}

Console.WriteLine();
return 0;

// ---------------------------------------------------------------------------

static void Row(string label, long ms) => Console.WriteLine($"  {label,-36} {ms,7} ms");

static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "~";

static SchemaSource MakeSource(string cs, string? label, int index)
{
    var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
    return new SchemaSource
    {
        Id = index == 0 ? "source" : $"target{index}",
        ConnectionString = cs,
        Server = b.DataSource,
        Database = b.InitialCatalog,
        Label = label ?? $"{b.DataSource}.{b.InitialCatalog}"
    };
}

static void PrintUsage()
{
    Console.WriteLine("""

        SchemaScope spike - measures read speed and normalizer accuracy.

          schemascope-spike --source "<connection string>"
                            --target "<connection string>" [--target ...]
                            [--label-source Dev] [--label-target Davi]
                            [--samples 5] [--json out.json]

        Example (Windows auth):
          schemascope-spike -s "Server=.;Database=Platform_Core;Integrated Security=true;TrustServerCertificate=true" -t "Server=.;Database=Platform_Core_1;Integrated Security=true;TrustServerCertificate=true"

        Example (SQL login):
          schemascope-spike -s "Server=10.0.0.5,1433;Database=Dev;User Id=sa;Password=xxx;TrustServerCertificate=true" -t "Server=10.0.0.6,1433;Database=Prod;User Id=sa;Password=xxx;TrustServerCertificate=true"

        """);
}
