<#
.SYNOPSIS
    DocFrame Production PostgreSQL Restore Script (PowerShell)
.DESCRIPTION
    Restores a compressed, custom-format (-Fc) pg_dump backup with safety confirmation.
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
    [switch]$Force
)

$ErrorActionPreference = "Stop"

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

if (-not (Test-Path $BackupFile)) {
    throw "Backup file not found: $BackupFile"
}

if (-not $Force) {
    $confirm = Read-Host "WARNING: This will drop and recreate existing objects in database '$Database' on ${HostName}:${Port}. Proceed? (y/N)"
    if ($confirm -ne "y" -and $confirm -ne "Y") {
        Write-Host "Restore cancelled by user." -ForegroundColor Yellow
        exit 0
    }
}

$env:PGPASSWORD = $Password
Write-Host "[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] Restoring database '$Database' from '$BackupFile'..." -ForegroundColor Cyan

$pgRestoreCmd = Get-Command "pg_restore" -ErrorAction SilentlyContinue
if ($pgRestoreCmd) {
    & pg_restore -h $HostName -p $Port -U $User -d $Database -Fc --clean --if-exists --no-owner --no-privileges $BackupFile
} else {
    Write-Host "pg_restore not found on host, executing via docker container 'docframe-postgres'..." -ForegroundColor Yellow
    docker cp $BackupFile "docframe-postgres:/tmp/restore.dump"
    docker exec -e PGPASSWORD=$Password docframe-postgres pg_restore -U $User -d $Database -Fc --clean --if-exists --no-owner --no-privileges "/tmp/restore.dump"
    docker exec docframe-postgres rm -f "/tmp/restore.dump"
}

Write-Host "[$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss'))] Database '$Database' restored successfully." -ForegroundColor Green
