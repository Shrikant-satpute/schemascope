using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using SchemaScope.Api;
using SchemaScope.Api.Services;
using SchemaScope.Scripting;

namespace SchemaScope.Tests;

/// <summary>Memory-optimized objects: found before anything runs, never left to fail with Msg 12331 part-way.</summary>
public class MemoryOptimizedPlanTests
{
    private static VerifiedFile File(string folder, string name, string obj, string text) =>
        new(new PlanFile(folder, $"objects\\{name}", name, obj, ""), ScriptText.SplitBatches(text));

    [Fact]
    public void A_file_on_a_memory_optimized_object_or_creating_one_is_refused_before_anything_runs()
    {
        var plain = File("03_missing", "001_t.sql", "dbo.T", "CREATE TABLE dbo.T (id int)\r\nGO\r\n");
        Assert.Null(ApplyService.MemoryOptimizedProblem([plain], new HashSet<string>()));

        var onHekaton = File("04_columntypes", "001_h.sql", "dbo.Hot", "ALTER TABLE dbo.Hot ALTER COLUMN v nvarchar(50)\r\nGO\r\n");
        var created = File("03_missing", "002_n.sql", "dbo.New",
            "CREATE TABLE dbo.New (id int NOT NULL PRIMARY KEY NONCLUSTERED) WITH (MEMORY_OPTIMIZED = ON)\r\nGO\r\n");
        var problem = ApplyService.MemoryOptimizedProblem([plain, onHekaton, created], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "DBO.HOT" });

        Assert.NotNull(problem);
        Assert.Contains("2 file(s)", problem);
        Assert.Contains("Msg 12331", problem);
        Assert.Contains("04_columntypes\\objects\\001_h.sql", problem);
        Assert.Contains("03_missing\\objects\\002_n.sql", problem);
        Assert.DoesNotContain("001_t.sql", problem);
        Assert.Contains("Nothing was applied", problem);
    }
}

/// <summary>
/// The apply's own safety stops, on throwaway LocalDB databases (SchemaScope_Test_&lt;id&gt;_Tgt) only, dropped afterwards.
/// </summary>
public sealed class ApplySafetyLocalDbTests
{
    private static readonly ApplyTarget Target = new() { Label = "Throwaway", Server = LocalDb.Server, Login = "Windows: test" };

    private static (List<PlanFile> Plan, List<VerifiedFile> Verified) Files(string root, params (string Folder, string File, string Text)[] files)
    {
        var written = files.Select(f => PlanFiles.Write(root, f.Folder, f.File, f.Text, "T")).ToList();
        var (plan, _) = ApplyPlans.Build(written, []);
        Assert.Null(ApplyService.Verify(plan, out var verified));
        return (plan, verified);
    }

    /// <summary>Another session holding a shared lock on dbo.T until it is disposed: an ALTER TABLE on it waits.</summary>
    private static async Task<SqlConnection> HoldAsync(string database)
    {
        var holder = new SqlConnection(LocalDb.Conn(database));
        await holder.OpenAsync();
        await using var cmd = new SqlCommand("BEGIN TRAN; SELECT COUNT(*) FROM dbo.T WITH (HOLDLOCK);", holder);
        await cmd.ExecuteScalarAsync();
        return holder;
    }

    private static async Task<ApplyResult> Blocked(string tgt, TempStore temp, TempFolder folder, Action<ApplyService, WriteMode, string> end)
    {
        var (plan, verified) = Files(folder.Path,
            ("04_columntypes", "001_a.sql", "ALTER TABLE dbo.T ADD c int\r\nGO\r\n"),
            ("03_missing", "001_b.sql", "CREATE TABLE dbo.B (id int)\r\nGO\r\n"));
        var mode = new WriteMode();
        mode.Enable();
        var service = new ApplyService(temp.Store, mode, NullLogger<ApplyService>.Instance);
        var progressId = Guid.NewGuid().ToString();

        await using var holder = await HoldAsync(tgt);
        var run = service.RunAsync(new ApplyJob("run", LocalDb.Conn(tgt), new ApplyTarget
        {
            Label = Target.Label, Server = Target.Server, Database = tgt, Login = Target.Login,
        }, plan, ["04_columntypes", "03_missing"], progressId), verified, null);
        await Task.Delay(1500);
        Assert.False(run.IsCompleted);   // waiting on the lock

        end(service, mode, progressId);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(run, finished);      // ended while the lock is still held, not when it goes away
        var result = await run;

        await using (var rollback = new SqlCommand("ROLLBACK", holder)) await rollback.ExecuteNonQueryAsync();
        await Task.Delay(500);
        Assert.Equal(DBNull.Value, LocalDb.Scalar(tgt, "SELECT COL_LENGTH('dbo.T', 'c')"));   // rolled back, never committed later
        Assert.Equal(DBNull.Value, LocalDb.Scalar(tgt, "SELECT OBJECT_ID('dbo.B')"));
        return result;
    }

    [LocalDbFact]
    public async Task Cancel_now_ends_a_file_stuck_on_a_lock_and_rolls_it_back()
    {
        var tgt = LocalDb.NewName(Guid.NewGuid().ToString("n")[..8], "Tgt");
        using var temp = new TempStore();
        using var folder = new TempFolder();
        try
        {
            LocalDb.Create(tgt, "CREATE TABLE dbo.T (id int); INSERT dbo.T VALUES (1);");

            var result = await Blocked(tgt, temp, folder, (service, _, id) => Assert.True(service.Stop(id, now: true)));

            Assert.False(result.Success);
            var file = Assert.Single(result.Applied);
            Assert.False(file.Ok);
            Assert.Contains("rolled back", file.Error);
            Assert.Contains("Stopped because you cancelled it", result.Message);
            Assert.Contains("04_columntypes\\objects\\001_a.sql was rolled back and 1 were not run", result.Message);
            Assert.Equal(result.Message, Assert.Single(temp.Store.GetApplyRuns()).Message);
        }
        finally
        {
            LocalDb.Drop(tgt);
        }
    }

    [LocalDbFact]
    public async Task Turning_write_mode_off_ends_the_running_file_instead_of_letting_it_commit_later()
    {
        var tgt = LocalDb.NewName(Guid.NewGuid().ToString("n")[..8], "Tgt");
        using var temp = new TempStore();
        using var folder = new TempFolder();
        try
        {
            LocalDb.Create(tgt, "CREATE TABLE dbo.T (id int); INSERT dbo.T VALUES (1);");

            var result = await Blocked(tgt, temp, folder, (_, mode, _) => mode.Disable());

            Assert.False(result.Success);
            Assert.False(Assert.Single(result.Applied).Ok);
            Assert.Contains("Stopped because write mode was turned off", result.Message);
        }
        finally
        {
            LocalDb.Drop(tgt);
        }
    }

    [LocalDbFact]
    public async Task A_file_the_audit_log_cannot_record_is_the_last_one_that_runs()
    {
        var tgt = LocalDb.NewName(Guid.NewGuid().ToString("n")[..8], "Tgt");
        using var temp = new TempStore();
        using var folder = new TempFolder();
        try
        {
            LocalDb.Create(tgt, "SELECT 1");
            var (plan, verified) = Files(folder.Path,
                ("04_columntypes", "001_a.sql", "CREATE TABLE dbo.A (id int)\r\nGO\r\n"),
                ("03_missing", "001_b.sql", "CREATE TABLE dbo.B (id int)\r\nGO\r\n"));
            var mode = new WriteMode();
            mode.Enable();
            var service = new ApplyService(temp.Store, mode, NullLogger<ApplyService>.Instance);

            // the per-file log becomes unwritable after the run has started
            using (var sqlite = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp.DbPath}"))
            {
                sqlite.Open();
                using var drop = sqlite.CreateCommand();
                drop.CommandText = "DROP TABLE apply_batches";
                drop.ExecuteNonQuery();
            }

            var result = await service.RunAsync(new ApplyJob("run", LocalDb.Conn(tgt), new ApplyTarget
            {
                Label = Target.Label, Server = Target.Server, Database = tgt, Login = Target.Login,
            }, plan, ["04_columntypes", "03_missing"], null), verified, null);

            Assert.False(result.Success);
            Assert.True(Assert.Single(result.Applied).Ok);
            Assert.Equal("03_missing\\objects\\001_b.sql", result.StoppedAt);
            Assert.Contains("Stopped because the apply log could not be written", result.Message);
            Assert.NotEqual(DBNull.Value, LocalDb.Scalar(tgt, "SELECT OBJECT_ID('dbo.A')"));
            Assert.Equal(DBNull.Value, LocalDb.Scalar(tgt, "SELECT OBJECT_ID('dbo.B')"));
        }
        finally
        {
            LocalDb.Drop(tgt);
        }
    }

    [LocalDbFact]
    public async Task The_target_is_known_by_the_name_the_server_reports()
    {
        var tgt = LocalDb.NewName(Guid.NewGuid().ToString("n")[..8], "Tgt");
        try
        {
            LocalDb.Create(tgt, "SELECT 1");
            var a = await ApplyService.InspectAsync(LocalDb.Conn(tgt));
            var b = await ApplyService.InspectAsync($"Server=(LocalDB)\\MSSQLLocalDB;Database={tgt};Trusted_Connection=yes;Encrypt=false");

            Assert.NotNull(a);
            Assert.NotNull(b);
            Assert.Equal(a.Key, b.Key, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("(localdb)", a.Key, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("|" + tgt, a.Key);
            Assert.Equal(a.Key, ApplyService.TargetKey(a, "(localdb)\\MSSQLLocalDB", tgt));
            Assert.Empty(a.InMemory);   // (LocalDB cannot compile a memory-optimized table; MemoryOptimizedPlanTests covers the check)

            Assert.Null(await ApplyService.InspectAsync(
                "Data Source=tcp:127.0.0.1,1;Initial Catalog=x;Integrated Security=true;Connect Timeout=1;Encrypt=false"));
        }
        finally
        {
            LocalDb.Drop(tgt);
        }
    }
}
