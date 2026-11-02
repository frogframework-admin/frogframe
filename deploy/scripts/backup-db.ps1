<#
.SYNOPSIS
    DocFrame Production PostgreSQL Backup Script (PowerShell)
.DESCRIPTION
    Creates a compressed, custom-format (-Fc) pg_dump backup with integrity verification and retention pruning.
#>
[CmdletBinding()]
param(
    [string]$BackupDir = "",
    [string]$HostName = "",
    [int]$Port = 0,
    [string]$Database = "",
    [string]$User = "",
    [string]$Password = "",
    [int]$RetentionDays = 14
)

$ErrorActionPreference = "Stop"

if (-not $BackupDir) {
    $BackupDir = Join-Path $PSScriptRoot "..\..\backups"
}
if (-not $HostName) {
    $HostName = if ($env:PGHOST) { $env:PGHOST } else { "127.0.0.1" }
}
if ($Port -le 0) {
    $Port = if ($env:PGPORT) { [int]$env:PGPORT } else { 5432 }
}
if (-not $Database) {
    $Database = if ($env:PGDATABASE) { $env:PGDATABASE } else { "docframe" }
}
if (-not $User) {
    $User = if ($env:PGUSER) { $env:PGUSER } else { "docframe" }
}
if (-not $Password) {
    $Password = if ($env:PGPASSWORD) { $env:PGPASSWORD } else { "docframe" }
}

if (-not (Test-Path $BackupDir)) {
    New-Item -ItemType Directory -Path $BackupDir -Force | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$backupFile = Join-Path $BackupDir "${Database}_${timestamp}.dump"

$env:PGPASSWORD = $Password

Write-Host "[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] Starting backup of '$Database' from ${HostName}:${Port}..." -ForegroundColor Cyan

# Run pg_dump (or through docker if pg_dump not in host PATH)
$pgDumpCmd = Get-Command "pg_dump" -ErrorAction SilentlyContinue
if ($pgDumpCmd) {
    & pg_dump -h $HostName -p $Port -U $User -d $Database -Fc -f $backupFile
} else {
    Write-Host "pg_dump not found on host, executing via docker container 'docframe-postgres'..." -ForegroundColor Yellow
    docker exec -e PGPASSWORD=$Password docframe-postgres pg_dump -U $User -d $Database -Fc -f "/tmp/backup.dump"
    docker cp "docframe-postgres:/tmp/backup.dump" $backupFile
    docker exec docframe-postgres rm -f "/tmp/backup.dump"
}

if (-not (Test-Path $backupFile)) {
    throw "Backup failed: $backupFile was not created"
}

$fileSize = (Get-Item $backupFile).Length / 1MB
Write-Host "[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] Backup created: $backupFile ($([Math]::Round($fileSize, 2)) MB)" -ForegroundColor Green

# Verify integrity
Write-Host "Verifying archive integrity..." -ForegroundColor Cyan
if ($pgDumpCmd) {
    & pg_restore --list $backupFile | Out-Null
} else {
    docker cp $backupFile "docframe-postgres:/tmp/verify.dump"
    docker exec docframe-postgres pg_restore --list "/tmp/verify.dump" | Out-Null
    docker exec docframe-postgres rm -f "/tmp/verify.dump"
}
Write-Host "Archive integrity verified." -ForegroundColor Green

# Retention policy
$cutoff = (Get-Date).AddDays(-$RetentionDays)
Get-ChildItem -Path $BackupDir -Filter "${Database}_*.dump" | Where-Object { $_.LastWriteTime -lt $cutoff } | ForEach-Object {
    Write-Host "Pruning old backup: $($_.FullName)" -ForegroundColor Yellow
    Remove-Item $_.FullName -Force
}
Write-Host "Backup rotation complete." -ForegroundColor Green
