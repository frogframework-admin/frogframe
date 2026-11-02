<#
.SYNOPSIS
    DocFrame Production PostgreSQL Restore Script (PowerShell)
.DESCRIPTION
    Restores a compressed, custom-format (-Fc) pg_dump backup in a single transaction.
    Exits with a non-zero code if any step fails.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$BackupFile,
    [string]$HostName = "",
    [int]$Port = 0,
    [string]$Database = "",
    [string]$User = "",
    [string]$Password = "",
    [string]$Container = "docframe-postgres",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Invoke-Native {
    param([string]$Description, [scriptblock]$Command)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed (exit code $LASTEXITCODE)"
    }
}

if (-not $HostName) { $HostName = if ($env:PGHOST) { $env:PGHOST } else { "127.0.0.1" } }
if ($Port -le 0) { $Port = if ($env:PGPORT) { [int]$env:PGPORT } else { 5432 } }
if (-not $Database) { $Database = if ($env:PGDATABASE) { $env:PGDATABASE } else { "docframe" } }
if (-not $User) { $User = if ($env:PGUSER) { $env:PGUSER } else { "docframe" } }
if (-not $Password) {
    if (-not $env:PGPASSWORD) { throw "Set PGPASSWORD or pass -Password." }
    $Password = $env:PGPASSWORD
}

if (-not (Test-Path $BackupFile)) {
    throw "Backup file not found: $BackupFile"
}

if (-not $Force) {
    $confirm = Read-Host "WARNING: This will drop and recreate objects in database '$Database' on ${HostName}:${Port}. Proceed? (y/N)"
    if ($confirm -ne "y" -and $confirm -ne "Y") {
        Write-Host "Restore cancelled by user." -ForegroundColor Yellow
        exit 0
    }
}

$env:PGPASSWORD = $Password
$restoreArgs = @("-U", $User, "-d", $Database, "--clean", "--if-exists", "--no-owner", "--no-privileges", "--single-transaction", "--exit-on-error")
Write-Host "[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] Restoring database '$Database' from '$BackupFile'..." -ForegroundColor Cyan

if (Get-Command "pg_restore" -ErrorAction SilentlyContinue) {
    Invoke-Native "Archive validation" { pg_restore --list $BackupFile | Out-Null }
    Invoke-Native "pg_restore" { pg_restore -h $HostName -p $Port @restoreArgs $BackupFile }
} else {
    Write-Host "pg_restore not found on host, using docker container '$Container'..." -ForegroundColor Yellow
    Invoke-Native "docker cp" { docker cp $BackupFile "${Container}:/tmp/restore.dump" }
    try {
        Invoke-Native "Archive validation" { docker exec $Container pg_restore --list /tmp/restore.dump | Out-Null }
        Invoke-Native "pg_restore" { docker exec -e PGPASSWORD=$Password $Container pg_restore @restoreArgs /tmp/restore.dump }
    } finally {
        docker exec $Container rm -f /tmp/restore.dump | Out-Null
    }
}

Write-Host "[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] Database '$Database' restored successfully." -ForegroundColor Green
Write-Host "Next: re-apply deploy/sql/reporting-readonly-user.sql and start the app so SchemaMigrator runs." -ForegroundColor Cyan
