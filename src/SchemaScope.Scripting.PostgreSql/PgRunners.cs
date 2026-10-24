using Npgsql;

namespace SchemaScope.Scripting.PostgreSql;

/// <summary>Where the psql runners connect by default: the compared target's host, port, user and TLS mode. No password.</summary>
public sealed record PgRunnerTarget(string Host, int Port, string User, string SslMode)
{
    /// <summary>From the target's connection string; the password in it is never read out.</summary>
    public static PgRunnerTarget From(ScriptEndpoint target)
    {
        try
        {
            var b = new NpgsqlConnectionStringBuilder(target.ConnectionString);
            var host = (b.Host ?? "").Split(',')[0].Trim();
            if (host.Contains(':') && !host.StartsWith('[')) host = host[..host.LastIndexOf(':')];
            return new PgRunnerTarget(host.Length > 0 ? host : ScriptGenerator.RunnerServer(target), b.Port,
                b.Username ?? "postgres", LibpqSslMode(b.SslMode));
        }
        catch (ArgumentException)
        {
            return new PgRunnerTarget(ScriptGenerator.RunnerServer(target), 5432, "postgres", "verify-full");
        }
    }

    /// <summary>libpq's spelling of the TLS mode SchemaScope connected with (psql reads it from PGSSLMODE).</summary>
    private static string LibpqSslMode(Npgsql.SslMode m) => m switch
    {
        Npgsql.SslMode.Disable => "disable",
        Npgsql.SslMode.Allow => "allow",
        Npgsql.SslMode.Prefer => "prefer",
        Npgsql.SslMode.Require => "require",
        Npgsql.SslMode.VerifyCA => "verify-ca",
        _ => "verify-full",
    };
}

/// <summary>
/// The psql runners (plan §5.5): each folder's <c>run_all.ps1</c> runs its <c>objects\*.sql</c> by leading number with
/// <c>psql -X -v ON_ERROR_STOP=1 --single-transaction -f</c>, so a file that fails changes nothing; <c>RUN.ps1</c> runs
/// the folders in order. No password is ever written: psql reads it from a PGPASSFILE that must be outside the run
/// folder (<c>-PassFile</c> or the environment), or the runner asks once and keeps it in this PowerShell process's
/// environment for psql only, clearing it at the end.
/// </summary>
public static class PgRunners
{
    /// <summary>
    /// The password step both runners share. <c>$runRoot</c> is the run folder; a PGPASSFILE inside it is refused, so a
    /// password file can never travel with the scripts.
    /// </summary>
    private const string PasswordStep = """
        function Use-PgPassword([string]$PassFile, [string]$RunRoot) {
            if ($PassFile) { $env:PGPASSFILE = $PassFile }
            if ($env:PGPASSFILE) {
                $full = [System.IO.Path]::GetFullPath($env:PGPASSFILE)
                $root = [System.IO.Path]::GetFullPath($RunRoot).TrimEnd('\') + '\'
                if ($full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
                    Write-Host "Refusing PGPASSFILE $full - it is inside the scripts folder; keep the password file elsewhere." -ForegroundColor Red
                    exit 1
                }
                if (-not (Test-Path -LiteralPath $full)) { Write-Host "PGPASSFILE $full does not exist." -ForegroundColor Red; exit 1 }
                return $false
            }
            if ($env:PGPASSWORD) { return $false }
            $secure = Read-Host "Password for $User on ${Server}:$Port (not stored)" -AsSecureString
            $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
            try { $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
            finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
            return $true
        }
        $env:PGSSLMODE = $SslMode
        $env:PGCLIENTENCODING = 'UTF8'
        $env:PGAPPNAME = 'SchemaScope'
        $env:PGOPTIONS = '-c lock_timeout=5s'
        """;

    /// <summary>One folder's <c>run_all.ps1</c>, CRLF.</summary>
    public static string Folder(PgRunnerTarget t) => Crlf($$"""
        param(
            [string]$Server = {{ScriptText.PsQuote(t.Host)}},
            [int]$Port = {{t.Port}},
            [Parameter(Mandatory=$true)][string]$Database,
            [string]$User = {{ScriptText.PsQuote(t.User)}},
            [string]$SslMode = {{ScriptText.PsQuote(t.SslMode)}},
            [string]$PassFile,
            [switch]$ContinueOnError
        )
        {{PasswordStep}}
        $prompted = Use-PgPassword $PassFile (Split-Path -Parent $PSScriptRoot)
        try {
            $dir = Join-Path $PSScriptRoot "objects"
            # by the leading number first, so 1000_x runs after 999_x and not before 101_x (as Apply orders them)
            $files = @(Get-ChildItem -LiteralPath $dir -Filter *.sql |
                Sort-Object @{ Expression = { if ($_.Name -match '^(\d{1,18})') { [long]$Matches[1] } else { [long]::MaxValue } } }, Name)
            if ($files.Count -eq 0) {
                Write-Host "No .sql files found in $dir - nothing was run." -ForegroundColor Red
                exit 1
            }
            $fail = 0
            foreach ($f in $files) {
                # psql cannot open a path of 260 characters or more (Windows' MAX_PATH; a PostgreSQL file name carries the
                # argument types), so such a file runs from a short copy in TEMP, removed straight after
                $path = $f.FullName
                $copy = $null
                if ($path.Length -ge 250) {
                    $copy = Join-Path ([System.IO.Path]::GetTempPath()) ("schemascope-" + [guid]::NewGuid().ToString("n") + ".sql")
                    [System.IO.File]::Copy("\\?\" + $path, $copy, $true)
                    $path = $copy
                }
                try {
                    # one transaction per file: a statement that fails rolls the whole file back
                    & psql -X -v ON_ERROR_STOP=1 --single-transaction -h $Server -p $Port -U $User -d $Database -f $path
                }
                finally {
                    if ($copy) { Remove-Item -LiteralPath $copy -ErrorAction SilentlyContinue }
                }
                if ($LASTEXITCODE -ne 0) {
                    Write-Host "FAILED  $($f.Name)" -ForegroundColor Red
                    $fail++
                    if (-not $ContinueOnError) { Write-Host "stopped - re-run with -ContinueOnError to skip past failures"; break }
                } else {
                    Write-Host "ok      $($f.Name)"
                }
            }
            Write-Host ""
            Write-Host "$($files.Count) files, $fail failed"
            exit $fail
        }
        finally {
            if ($prompted) { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
        }

        """);

    /// <summary>The root <c>RUN.ps1</c>, CRLF: asks for the password once, then runs <paramref name="folders"/> in order.</summary>
    public static string Root(PgRunnerTarget t, IReadOnlyList<string> folders) => Crlf($$"""
        param(
            [string]$Server = {{ScriptText.PsQuote(t.Host)}},
            [int]$Port = {{t.Port}},
            [Parameter(Mandatory=$true)][string]$Database,
            [string]$User = {{ScriptText.PsQuote(t.User)}},
            [string]$SslMode = {{ScriptText.PsQuote(t.SslMode)}},
            [string]$PassFile
        )
        {{PasswordStep}}
        $folders = @({{string.Join(",", folders.Select(f => "\"" + f + "\""))}})
        $prompted = Use-PgPassword $PassFile $PSScriptRoot
        try {
            $failed = 0
            foreach ($folder in $folders) {
                Write-Host ""
                Write-Host "=== $folder ===" -ForegroundColor Cyan
                & (Join-Path $PSScriptRoot "$folder\run_all.ps1") -Server $Server -Port $Port -Database $Database -User $User -SslMode $SslMode -ContinueOnError
                $failed += $LASTEXITCODE
            }
            Write-Host ""
            Write-Host "$failed file(s) failed. Re-run the SchemaScope compare to see what is still different."
            exit $failed
        }
        finally {
            if ($prompted) { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
        }

        """);

    private static string Crlf(string s) => s.ReplaceLineEndings("\n").Replace("\n", "\r\n");
}
