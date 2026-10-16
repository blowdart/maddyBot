# Copyright (c) Barry Dorrans. All rights reserved.
# Licensed under the MIT License.

<#
.SYNOPSIS
Runs application/package integration tests using isolated PostgreSQL and SQLite storage.
.DESCRIPTION
Chooses Docker, then WSL containers (wslc, WSL 2.9.3+), unless explicitly
overridden. Creates a temporary PostgreSQL 16 container.
SQLite tests use separate local files. Containers are removed only when all
selected tests run and pass with no skips. On failure, containers and TRX
results are retained. Existing bot databases and binaries are never replaced.
.PARAMETER ContainerProvider
Auto tries Docker then WSL. An explicit host disables fallback.
.PARAMETER AllTests
Runs the full solution suite against the isolated databases rather than only storage tests.
.EXAMPLE
.\TestDatabaseProviders.ps1 -ContainerProvider WSL
#>
[CmdletBinding()]
param(
    [ValidateSet('Auto', 'Docker', 'WSL')]
    [string] $ContainerProvider = 'Auto',
    [switch] $AllTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not (Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET SDK pinned in global.json.'
}
$engine = $null
$candidates = switch ($ContainerProvider) {
    'Docker' { 'docker' }
    'WSL' { 'wslc' }
    default { 'docker'; 'wslc' }
}
foreach ($candidate in $candidates) {
    $command = Get-Command $candidate -CommandType Application -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $command -and $candidate -eq 'wslc' -and $env:ProgramFiles) {
        $path = Join-Path $env:ProgramFiles 'WSL\wslc.exe'
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $command = Get-Command $path -CommandType Application
        }
    }
    if (-not $command) { continue }
    [string[]] $probe = if ($candidate -eq 'wslc') { @('container', 'list') } else { @('info') }
    $probeOutput = & $command.Source @probe 2>&1
    if ($LASTEXITCODE -eq 0) {
        $engine = $command.Source
        $engineName = $candidate
        break
    }
    Write-Warning "$candidate is installed but unavailable."
    Write-Verbose ($probeOutput -join [Environment]::NewLine)
}
if (-not $engine) {
    throw "No usable container host for '$ContainerProvider'. Start Docker or install/configure WSL containers. Explicit selection does not fall back."
}

$runId = [Guid]::NewGuid().ToString('N')
$runDirectory = Join-Path ([IO.Path]::GetTempPath()) "maddybot-provider-tests-$runId"
$resultsDirectory = Join-Path $runDirectory 'results'
$buildDirectory = Join-Path $runDirectory 'build'
$previousConnection = $env:MADDYBOT_TEST_CONNECTION
$containers = @()
$removed = @()
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
Write-Host "Using $engineName; results and separate build output: $runDirectory"

try {
    $name = "maddybot-provider-tests-postgres-$runId"
    $password = [Guid]::NewGuid().ToString('N')
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try {
        $listener.Start()
        $port = ([Net.IPEndPoint] $listener.LocalEndpoint).Port
    }
    finally { $listener.Stop() }
    $runArguments = @('run', '--detach', '--name', $name,
        '--publish', "127.0.0.1:${port}:5432")
    if ($engineName -eq 'wslc') { $runArguments += '--rm' }
    $runArguments += @('--env', 'POSTGRES_DB=maddybot_tests', '--env', 'POSTGRES_USER=maddybot_test',
        '--env', "POSTGRES_PASSWORD=$password", '--env', 'PGDATA=/tmp/maddybot-test-data',
        'docker.io/library/postgres:16')
    $containers += $name
    & $engine @runArguments
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create isolated PostgreSQL container.' }
    $ready = $false
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(180)
    do {
        & $engine exec $name pg_isready --host 127.0.0.1 `
            --username maddybot_test --dbname maddybot_tests *> $null
        if ($LASTEXITCODE -eq 0) { $ready = $true; break }
        Start-Sleep -Seconds 1
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if (-not $ready) { throw 'PostgreSQL did not become ready within 180 seconds.' }
    $env:MADDYBOT_TEST_CONNECTION = "Host=127.0.0.1;Port=$port;Database=maddybot_tests;Username=maddybot_test;Password=$password;SSL Mode=Disable"
    Write-Host "PostgreSQL ready on loopback port $port."

    Push-Location $repositoryRoot
    try {
        $testSelection = if ($AllTests) { @() } else {
            @('--filter', 'FullyQualifiedName~MaddyBot.Tests.PackageIntegrationTests')
        }
        & dotnet test (Join-Path $repositoryRoot 'MaddyBot.slnx') `
            @testSelection `
            --artifacts-path $buildDirectory --results-directory $resultsDirectory `
            --logger 'trx;LogFileName=provider-tests.trx'
        $testExitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    if ($testExitCode -ne 0) { throw 'Provider tests failed; containers and results were retained.' }
    $resultFiles = @(Get-ChildItem -LiteralPath $resultsDirectory -Filter '*.trx' -Recurse)
    if ($resultFiles.Count -ne 1) { throw 'Expected one provider TRX file; retaining containers.' }
    [xml] $results = Get-Content -LiteralPath $resultFiles[0].FullName -Raw
    $counters = $results.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $counters -or [int] $counters.total -le 0 -or [int] $counters.passed -ne [int] $counters.total) {
        throw 'Provider tests did not all run and pass (including possible skips); retaining containers.'
    }
    foreach ($name in $containers) {
        if ($engineName -eq 'wslc') { & $engine container stop $name }
        else { & $engine rm --force --volumes $name }
        if ($LASTEXITCODE -ne 0) { throw "Tests passed but container '$name' could not be removed." }
        $removed += $name
    }
    Write-Host "All $($counters.total) provider tests passed. Temporary containers removed."
}
finally {
    $env:MADDYBOT_TEST_CONNECTION = $previousConnection
    foreach ($name in $containers) {
        if ($removed -contains $name) { continue }
        Write-Warning "Container '$name' retained (or partially created). Results: $resultsDirectory"
        Write-Host "Inspect: & '$engine' container logs $name"
        if ($engineName -eq 'wslc') { Write-Host "Remove after diagnosis: & '$engine' container stop $name" }
        else { Write-Host "Remove after diagnosis: & '$engine' rm --force --volumes $name" }
    }
}
