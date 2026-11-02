<#
.SYNOPSIS
    DocFrame Production PostgreSQL Backup Script (PowerShell)
.DESCRIPTION
    Creates a compressed, custom-format (-Fc) pg_dump backup, verifies the archive, and prunes old backups.
    Exits with a non-zero code if any step fails.
#>
[CmdletBinding()]
param(
    [string]$BackupDir = "",
    [string]$HostName = "",
    [int]$Port = 0,
    [string]$Database = "",
    [string]$User = "",
    [string]$Password = "",
    [string]$Container = "docframe-postgres",
    [int]$RetentionDays = 14
)

$ErrorActionPreference = "Stop"

function Invoke-Native {
    param([string]$Description, [scriptblock]$Command)
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed (exit code $LASTEXITCODE)"
    }
}

if (-not $BackupDir) { $BackupDir = Join-Path $PSScriptRoot "..\..\backups" }
if (-not $HostName) { $HostName = if ($env:PGHOST) { $env:PGHOST } else { "127.0.0.1" } }
if ($Port -le 0) { $Port = if ($env:PGPORT) { [int]$env:PGPORT } else { 5432 } }
if (-not $Database) { $Database = if ($env:PGDATABASE) { $env:PGDATABASE } else { "docframe" } }
if (-not $User) { $User = if ($env:PGUSER) { $env:PGUSER } else { "docframe" } }
if (-not $Password) {
    if (-not $env:PGPASSWORD) { throw "Set PGPASSWORD or pass -Password." }
    $Password = $env:PGPASSWORD
}

if (-not (Test-Path $BackupDir)) {
    New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
}
$BackupDir = (Resolve-Path $BackupDir).Path

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$backupFile = Join-Path $BackupDir "${Database}_${timestamp}.dump"
$env:PGPASSWORD = $Password

Write-Host "[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] Starting backup of '$Database' from ${HostName}:${Port}..." -ForegroundColor Cyan

try {
    if (Get-Command "pg_dump" -ErrorAction SilentlyContinue) {
        Invoke-Native "pg_dump" { pg_dump -h $HostName -p $Port -U $User -d $Database -Fc -f $backupFile }
        Invoke-Native "Archive verification" { pg_restore --list $backupFile | Out-Null }
    } else {
        Write-Host "pg_dump not found on host, using docker container '$Container'..." -ForegroundColor Yellow
        try {
            Invoke-Native "pg_dump" { docker exec -e PGPASSWORD=$Password $Container pg_dump -U $User -d $Database -Fc -f /tmp/backup.dump }
            Invoke-Native "Archive verification" { docker exec $Container pg_restore --list /tmp/backup.dump | Out-Null }
            Invoke-Native "docker cp" { docker cp "${Container}:/tmp/backup.dump" $backupFile }
        } finally {
            docker exec $Container rm -f /tmp/backup.dump | Out-Null
        }
    }
} catch {
    # Never leave a partial/corrupt file that looks like a valid backup.
    if (Test-Path $backupFile) { Remove-Item $backupFile -Force }
    throw
}

if (-not (Test-Path $backupFile) -or (Get-Item $backupFile).Length -eq 0) {
    throw "Backup failed: $backupFile is missing or empty"
}

$fileSize = (Get-Item $backupFile).Length / 1MB
Write-Host "[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] Backup verified: $backupFile ($([Math]::Round($fileSize, 2)) MB)" -ForegroundColor Green

$cutoff = (Get-Date).AddDays(-$RetentionDays)
Get-ChildItem -Path $BackupDir -Filter "${Database}_*.dump" | Where-Object { $_.LastWriteTime -lt $cutoff } | ForEach-Object {
    Write-Host "Pruning old backup: $($_.FullName)" -ForegroundColor Yellow
    Remove-Item $_.FullName -Force
}
Write-Host "Backup rotation complete." -ForegroundColor Green
