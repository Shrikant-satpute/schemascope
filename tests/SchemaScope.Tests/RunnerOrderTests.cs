using System.Diagnostics;
using SchemaScope.Scripting;

namespace SchemaScope.Tests;

/// <summary>run_all.ps1 runs a folder's files in the order Apply does: by number, so 1000_x comes after 999_x.</summary>
public class RunnerOrderTests
{
    [Fact]
    public void Run_all_orders_files_by_their_number_not_by_name()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TempFolder();
        var objects = Directory.CreateDirectory(Path.Combine(temp.Path, "objects")).FullName;
        foreach (var name in new[] { "999_a.sql", "1000_b.sql", "100_c.sql", "101_d.sql", "notes.sql" })
            File.WriteAllText(Path.Combine(objects, name), "SELECT 1\r\n");
        File.WriteAllText(Path.Combine(temp.Path, "run_all.ps1"), ScriptText.Runner("nowhere"));

        // a stand-in sqlcmd that only records which file it was given
        var bin = Directory.CreateDirectory(Path.Combine(temp.Path, "bin")).FullName;
        var log = Path.Combine(temp.Path, "ran.txt");
        File.WriteAllText(Path.Combine(bin, "sqlcmd.cmd"), "@echo %*>>\"%SS_RUNLOG%\"\r\n@exit /b 0\r\n");

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{Path.Combine(temp.Path, "run_all.ps1")}\" -Database x")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["SS_RUNLOG"] = log;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        Assert.True(p.WaitForExit(60_000), output);
        Assert.Equal(0, p.ExitCode);

        var ran = File.ReadAllLines(log).Select(l => Path.GetFileName(l.Trim().Trim('"'))).ToList();
        Assert.Equal(["100_c.sql", "101_d.sql", "999_a.sql", "1000_b.sql", "notes.sql"], ran);
    }
}
