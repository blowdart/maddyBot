# Copyright (c) Barry Dorrans. All rights reserved.
# Licensed under the MIT License.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Docker', 'WSL')]
    [string] $HostType,
    [ValidateRange(1024, 65535)]
    [int] $Port = 33060,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]+$')]
    [string] $ContainerName = 'maddybot-mysql',
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_.-]+$')]
    [string] $VolumeName = 'maddybot-mysql84'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$MySqlVersion = '8.4'

$engineName = switch ($HostType) {
    'Docker' { 'docker' }
    'WSL' { 'wslc' }
}
$command = Get-Command $engineName -CommandType Application -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $command) {
    $installPaths = @()
    if ($HostType -eq 'WSL' -and $env:ProgramFiles) {
        $installPaths += Join-Path $env:ProgramFiles 'WSL\wslc.exe'
    }
    foreach ($installPath in $installPaths) {
        if (Test-Path -LiteralPath $installPath -PathType Leaf) {
            $command = Get-Command $installPath -CommandType Application
            break
        }
    }
}
if (-not $command) {
    throw "$HostType is not installed. No other host will be selected."
}
$engine = $command.Source
[string[]] $probeArguments = if ($HostType -eq 'WSL') { @('container', 'list') } else { @('info') }
& $engine @probeArguments | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "$HostType is not usable. Start/configure the selected host before running this script."
}
$existing = & $engine container ls --all --filter "name=^/$ContainerName$" --quiet
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect $HostType containers."
}
if ($existing) {
    throw "Container '$ContainerName' already exists. Start it with '& `"$engine`" container start $ContainerName'; this script never deletes existing data."
}

$volumes = & $engine volume ls --quiet
if ($LASTEXITCODE -ne 0) {
    throw "Unable to inspect $HostType volumes."
}
if ($volumes -contains $VolumeName) {
    throw "Volume '$VolumeName' already exists. Choose a new volume name; generated passwords cannot replace credentials in an existing database."
}

& $engine volume create $VolumeName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to create the MySQL volume.'
}

$previousRootPassword = $env:MYSQL_ROOT_PASSWORD
$previousPassword = $env:MYSQL_PASSWORD
$rootPasswordBytes = New-Object byte[] 32
$passwordBytes = New-Object byte[] 32
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try {
    $random.GetBytes($rootPasswordBytes)
    $random.GetBytes($passwordBytes)
    $rootPassword = [Convert]::ToBase64String($rootPasswordBytes)
    $password = [Convert]::ToBase64String($passwordBytes)
    $env:MYSQL_ROOT_PASSWORD = $rootPassword
    $env:MYSQL_PASSWORD = $password
    $runArguments = @(
        'run', '--detach', '--name', $ContainerName,
        '--publish', "127.0.0.1:${Port}:3306",
        '--env', 'MYSQL_DATABASE=maddybot', '--env', 'MYSQL_USER=maddybot',
        '--env', 'MYSQL_ROOT_PASSWORD', '--env', 'MYSQL_PASSWORD',
        '--mount', "type=volume,source=$VolumeName,target=/var/lib/mysql",
        '--health-cmd', 'mysqladmin ping --silent --host=127.0.0.1 --user=root --password="$MYSQL_ROOT_PASSWORD"',
        '--health-interval', '5s', '--health-timeout', '3s', '--health-retries', '12'
    )
    if ($HostType -ne 'WSL') {
        $runArguments += @('--restart', 'unless-stopped')
    }
    $runArguments += "docker.io/library/mysql:$MySqlVersion"
    & $engine @runArguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to create the MySQL container.'
    }
}
finally {
    $random.Dispose()
    [Array]::Clear($rootPasswordBytes, 0, $rootPasswordBytes.Length)
    [Array]::Clear($passwordBytes, 0, $passwordBytes.Length)
    $env:MYSQL_ROOT_PASSWORD = $previousRootPassword
    $env:MYSQL_PASSWORD = $previousPassword
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(90)
do {
    $inspection = & $engine container inspect $ContainerName
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to inspect MySQL readiness.'
    }
    $state = ($inspection -join "`n" | ConvertFrom-Json)[0].State
    $status = $state.Health.Status
    if ($status -eq 'healthy') {
        Write-Host "MySQL $MySqlVersion is ready on 127.0.0.1:$Port. Database/user: maddybot. Named volume: $VolumeName."
        Write-Warning 'The following connection string contains a password. Store it in user secrets; do not commit or share it.'
        Write-Output "Server=127.0.0.1;Port=$Port;Database=maddybot;User ID=maddybot;Password=$password;SslMode=Required"
        return
    }
    if ($status -eq 'unhealthy') {
        throw "MySQL is unhealthy. Inspect '& `"$engine`" container logs $ContainerName'. The container and volume were retained."
    }
    Start-Sleep -Seconds 2
} while ([DateTimeOffset]::UtcNow -lt $deadline)

throw "MySQL did not become healthy in 90 seconds. Inspect '& `"$engine`" container logs $ContainerName'. The container and volume were retained."
