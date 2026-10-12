# Copyright (c) Barry Dorrans. All rights reserved.
# Licensed under the MIT License.

<#
.SYNOPSIS
Creates a new local SQLite file without overwriting existing data.
.DESCRIPTION
Requires sqlite3 on PATH. Initializes WAL journaling; the SDK creates its
tables on first startup. Keep the file on persistent local storage, not a
network filesystem or App Platform's ephemeral filesystem.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$command = Get-Command sqlite3 -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $command) {
    throw 'sqlite3 is required. Install the SQLite command-line tools and add sqlite3 to PATH.'
}
$fullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
if (Test-Path -LiteralPath $fullPath) {
    throw "Path '$fullPath' already exists. This script never replaces an existing database."
}
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($fullPath)) -PathType Container)) {
    throw 'The parent directory must already exist on persistent local storage.'
}

# CreateNew reserves exactly this path, so concurrent invocation cannot overwrite it.
$file = [IO.File]::Open($fullPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$file.Dispose()
& $command.Source $fullPath 'PRAGMA journal_mode=WAL; PRAGMA user_version=0;' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "SQLite initialization failed. The file '$fullPath' was retained for diagnosis."
}
Write-Host "SQLite file created: $fullPath. The SDK initializes bot tables on first startup."
$escaped = $fullPath.Replace('"', '""')
Write-Output "Data Source=`"$escaped`";Default Timeout=15;Pooling=False"
