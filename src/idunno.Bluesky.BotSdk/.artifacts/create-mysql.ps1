# Copyright (c) Barry Dorrans. All rights reserved.
# Licensed under the MIT License.

<#
.SYNOPSIS
Creates a persistent MySQL 8.4 development container with fresh credentials.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Docker', 'WSL')]
    [string] $HostType,
    [ValidateRange(1024, 65535)]
    [int] $Port = 33069,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]+$')]
    [string] $ContainerName = 'maddybot-mysql',
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]+$')]
    [string] $VolumeName = 'maddybot-mysql84'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$engineName = if ($HostType -eq 'Docker') { 'docker' } else { 'wslc' }
$command = Get-Command $engineName -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $command -and $HostType -eq 'WSL' -and $env:ProgramFiles) {
    $path = Join-Path $env:ProgramFiles 'WSL\wslc.exe'
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $command = Get-Command $path -CommandType Application
    }
}
if (-not $command) { throw "$HostType is not installed. No other host will be selected." }
$engine = $command.Source
[string[]] $probe = if ($HostType -eq 'WSL') { @('container', 'list') } else { @('info') }
& $engine @probe | Out-Null
if ($LASTEXITCODE -ne 0) { throw "$HostType is not usable. Start/configure the selected host first." }
$existing = & $engine container ls --all --filter "name=^/$ContainerName$" --quiet
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect existing containers.' }
if ($existing) { throw "Container '$ContainerName' already exists; this script never replaces existing data." }
$volumes = & $engine volume ls --quiet
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect existing volumes.' }
if ($volumes -contains $VolumeName) { throw "Volume '$VolumeName' already exists; fresh credentials cannot replace an existing database password." }
& $engine volume create $VolumeName | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to create the MySQL volume.' }
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$rootPassword = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$runArguments = @(
    'run', '--detach', '--name', $ContainerName,
    '--publish', "127.0.0.1:${Port}:3306",
    '--env', 'MYSQL_DATABASE=maddybot', '--env', 'MYSQL_USER=maddybot',
    '--env', "MYSQL_PASSWORD=$password", '--env', "MYSQL_ROOT_PASSWORD=$rootPassword",
    '--mount', "type=volume,source=$VolumeName,target=/var/lib/mysql"
)
if ($HostType -eq 'Docker') { $runArguments += @('--restart', 'unless-stopped') }
$runArguments += 'docker.io/library/mysql:8.4'
& $engine @runArguments | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to create MySQL. The volume was retained.' }

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(180)
do {
    # Wait for authenticated TCP, not the bootstrap server's temporary Unix socket.
    & $engine exec --env "MYSQL_PWD=$password" $ContainerName mysql `
        --protocol TCP --host '127.0.0.1' --user maddybot --database maddybot --execute 'SELECT 1' *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "MySQL 8.4 is ready on 127.0.0.1:$Port. Database/user: maddybot. Named volume: $VolumeName."
        Write-Warning 'The connection string contains a development password. Store it in user secrets; do not commit or share it.'
        Write-Output "Server=127.0.0.1;Port=$Port;Database=maddybot;User ID=maddybot;Password=$password;SslMode=None;AllowPublicKeyRetrieval=True"
        return
    }
    Start-Sleep -Seconds 2
} while ([DateTimeOffset]::UtcNow -lt $deadline)
throw "MySQL did not become ready. Inspect '& `"$engine`" container logs $ContainerName'. The container and volume were retained."
