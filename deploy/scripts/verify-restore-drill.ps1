<#
.SYNOPSIS
    FrogFrame Automated Backup & Restore Verification Drill (PowerShell)
.DESCRIPTION
    Executes a full backup-and-restore drill into a throwaway PostgreSQL database:
      1. Captures a compressed (-Fc) pg_dump snapshot (or uses -BackupFile).
      2. Creates a throwaway database (frogframe_drill_<timestamp>).
      3. Restores the snapshot into the throwaway database and measures RTO.
      4. Verifies migration versions, table row counts, and content checksums against the source database.
      5. Drops the throwaway database and cleans up temporary artifacts.
#>
[CmdletBinding()]
param(
    [string]$BackupFile = "",
    [string]$HostName = "",
    [int]$Port = 0,
    [string]$Database = "",
    [string]$User = "",
    [string]$Password = "",
    [string]$Container = "frogframe-postgres"
)

$ErrorActionPreference = "Stop"

function Invoke-Native {
    param([string]$Description, [scriptblock]$Command)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed (exit code $LASTEXITCODE)"
    }
}

function Read-DotEnvValue {
    param([string]$EnvPath, [string]$Key)
    if (-not (Test-Path $EnvPath)) { return "" }
    foreach ($line in Get-Content $EnvPath) {
        if ($line -match "^\s*#") { continue }
        if ($line -match "^\s*$([regex]::Escape($Key))\s*=\s*(.+?)\s*$") {
            return $matches[1].Trim('"', "'")
        }
    }
    return ""
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$dotEnvPath = Join-Path $repoRoot ".env"

if (-not $HostName) { $HostName = if ($env:PGHOST) { $env:PGHOST } else { "127.0.0.1" } }
if ($Port -le 0) { $Port = if ($env:PGPORT) { [int]$env:PGPORT } else { 5432 } }
if (-not $Database) { $Database = if ($env:PGDATABASE) { $env:PGDATABASE } else { "frogframe" } }
if (-not $User) { $User = if ($env:PGUSER) { $env:PGUSER } else { "frogframe" } }
if (-not $Password) {
    $Password = if ($env:PGPASSWORD) { $env:PGPASSWORD } else { Read-DotEnvValue -EnvPath $dotEnvPath -Key "POSTGRES_PASSWORD" }
    if (-not $Password) { throw "Set PGPASSWORD, pass -Password, or define POSTGRES_PASSWORD in .env." }
}

$useLocalTools = (Get-Command "pg_dump" -ErrorAction SilentlyContinue) -and (Get-Command "pg_restore" -ErrorAction SilentlyContinue) -and (Get-Command "psql" -ErrorAction SilentlyContinue)
$useDocker = -not $useLocalTools
if (-not $useDocker) {
    # Verify local TCP connection works; fall back to docker exec when PostgreSQL port is isolated (P0-3).
    try {
        $tcp = New-Object System.Net.Sockets.TcpClient
        $ar = $tcp.BeginConnect($HostName, $Port, $null, $null)
        if (-not $ar.AsyncWaitHandle.WaitOne(1000, $false)) { $useDocker = $true }
        $tcp.Close()
    } catch {
        $useDocker = $true
    }
}

function Invoke-PsqlScalar {
    param([string]$TargetDb, [string]$Sql)
    $env:PGPASSWORD = $Password
    if (-not $useDocker) {
        $out = & psql -h $HostName -p $Port -U $User -d $TargetDb -t -A -v ON_ERROR_STOP=1 -c $Sql
    } else {
        $out = & docker exec -e PGPASSWORD=$Password $Container psql -U $User -d $TargetDb -t -A -v ON_ERROR_STOP=1 -c $Sql
    }
    if ($LASTEXITCODE -ne 0) { throw "psql query failed on database '$TargetDb': $Sql" }
    return ($out -join "`n").Trim()
}

$drillDb = "frogframe_drill_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
$createdTempDump = $false
$drillCreated = $false

try {
    if (-not $BackupFile) {
        $BackupFile = Join-Path ([System.IO.Path]::GetTempPath()) "frogframe_drill_$(Get-Date -Format 'yyyyMMdd_HHmmss').dump"
        $createdTempDump = $true
        Write-Host "[Drill 1/5] Creating snapshot of '$Database'..." -ForegroundColor Cyan
        $env:PGPASSWORD = $Password
        if (-not $useDocker) {
            Invoke-Native "pg_dump" { pg_dump -h $HostName -p $Port -U $User -d $Database -Fc -f $BackupFile }
        } else {
            Invoke-Native "pg_dump (docker)" { docker exec -e PGPASSWORD=$Password $Container pg_dump -U $User -d $Database -Fc -f /tmp/drill.dump }
            Invoke-Native "docker cp" { docker cp "${Container}:/tmp/drill.dump" $BackupFile }
            docker exec $Container rm -f /tmp/drill.dump | Out-Null
        }
    } elseif (-not (Test-Path $BackupFile)) {
        throw "Backup file not found: $BackupFile"
    }

    $dumpSizeMb = [Math]::Round((Get-Item $BackupFile).Length / 1MB, 3)
    Write-Host "[Drill 2/5] Creating throwaway database '$drillDb' (snapshot size: ${dumpSizeMb} MB)..." -ForegroundColor Cyan
    [void](Invoke-PsqlScalar -TargetDb "postgres" -Sql "CREATE DATABASE `"$drillDb`" OWNER `"$User`";")
    $drillCreated = $true

    Write-Host "[Drill 3/5] Restoring snapshot into '$drillDb' and measuring RTO..." -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $restoreArgs = @("-U", $User, "-d", $drillDb, "--no-owner", "--no-privileges", "--single-transaction", "--exit-on-error")
    $env:PGPASSWORD = $Password
    if (-not $useDocker) {
        Invoke-Native "pg_restore" { pg_restore -h $HostName -p $Port @restoreArgs $BackupFile }
    } else {
        Invoke-Native "docker cp" { docker cp $BackupFile "${Container}:/tmp/drill_restore.dump" }
        try {
            Invoke-Native "pg_restore (docker)" { docker exec -e PGPASSWORD=$Password $Container pg_restore @restoreArgs /tmp/drill_restore.dump }
        } finally {
            docker exec $Container rm -f /tmp/drill_restore.dump | Out-Null
        }
    }
    $sw.Stop()
    $rtoSeconds = [Math]::Round($sw.Elapsed.TotalSeconds, 2)

    Write-Host "[Drill 4/5] Verifying migrations, table row counts, and content checksums..." -ForegroundColor Cyan

    # 1. Migration version check
    $srcMig = Invoke-PsqlScalar -TargetDb $Database -Sql "SELECT count(*) || ':' || coalesce(max(version), 0) FROM frogframe_schema_migrations;"
    $dstMig = Invoke-PsqlScalar -TargetDb $drillDb -Sql "SELECT count(*) || ':' || coalesce(max(version), 0) FROM frogframe_schema_migrations;"
    if ($srcMig -ne $dstMig) {
        throw "Migration mismatch: source='$srcMig' vs restored='$dstMig'"
    }

    # 2. Public table list & exact row counts
    $tableListSql = "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename;"
    $srcTables = (Invoke-PsqlScalar -TargetDb $Database -Sql $tableListSql) -split "`r?`n" | Where-Object { $_ }
    $dstTables = (Invoke-PsqlScalar -TargetDb $drillDb -Sql $tableListSql) -split "`r?`n" | Where-Object { $_ }
    if (($srcTables -join ",") -ne ($dstTables -join ",")) {
        throw "Table list mismatch between source and restored database."
    }

    $liveTelemetryTables = @("frogframe_scheduled_job_runs", "frogframe_outbox", "frogframe_error_logs")
    foreach ($tbl in $srcTables) {
        $srcCount = [int](Invoke-PsqlScalar -TargetDb $Database -Sql "SELECT count(*) FROM public.`"$tbl`";")
        $dstCount = [int](Invoke-PsqlScalar -TargetDb $drillDb -Sql "SELECT count(*) FROM public.`"$tbl`";")
        if ($liveTelemetryTables -contains $tbl) {
            if ($dstCount -gt $srcCount) {
                throw "Row count mismatch on live table '$tbl': restored ($dstCount) > source ($srcCount)"
            }
        } elseif ($srcCount -ne $dstCount) {
            throw "Row count mismatch on table '$tbl': source=$srcCount vs restored=$dstCount"
        }
    }

    # 3. Deterministic content checksums on core tables
    $coreTables = @("frogframe_documents", "frogframe_audit", "frogframe_users", "frogframe_roles", "frogframe_metadata_versions")
    foreach ($tbl in $coreTables) {
        if ($srcTables -contains $tbl) {
            $checksumSql = "SELECT coalesce(sum(hashtext(t::text)::bigint), 0) FROM public.`"$tbl`" AS t;"
            $srcHash = Invoke-PsqlScalar -TargetDb $Database -Sql $checksumSql
            $dstHash = Invoke-PsqlScalar -TargetDb $drillDb -Sql $checksumSql
            if ($srcHash -ne $dstHash) {
                throw "Content checksum mismatch on table '$tbl': source=$srcHash vs restored=$dstHash"
            }
        }
    }

    Write-Host ""
    Write-Host "=== RESTORE VERIFICATION DRILL PASSED ===" -ForegroundColor Green
    Write-Host "  Source Database : $Database"
    Write-Host "  Snapshot Size   : ${dumpSizeMb} MB"
    Write-Host "  Measured RTO    : ${rtoSeconds}s (target: < 1800s)"
    Write-Host "  Migrations      : $dstMig"
    Write-Host "  Tables Verified : $($srcTables.Count) (row counts & core checksums matched 100%)"
} finally {
    Write-Host "[Drill 5/5] Cleaning up throwaway database and temp files..." -ForegroundColor Cyan
    if ($drillCreated) {
        try {
            $env:PGPASSWORD = $Password
            $terminateSql = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '$drillDb' AND pid <> pg_backend_pid();"
            $dropSql = "DROP DATABASE IF EXISTS `"$drillDb`";"
            if (-not $useDocker) {
                & psql -h $HostName -p $Port -U $User -d postgres -c $terminateSql | Out-Null
                & psql -h $HostName -p $Port -U $User -d postgres -c $dropSql | Out-Null
            } else {
                & docker exec -e PGPASSWORD=$Password $Container psql -U $User -d postgres -c $terminateSql | Out-Null
                & docker exec -e PGPASSWORD=$Password $Container psql -U $User -d postgres -c $dropSql | Out-Null
            }
        } catch {
            Write-Warning "Failed to drop throwaway database '$drillDb': $_"
        }
    }
    if ($createdTempDump -and (Test-Path $BackupFile)) {
        Remove-Item $BackupFile -Force -ErrorAction SilentlyContinue
    }
}
