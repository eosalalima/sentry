[CmdletBinding()]
param(
    [Parameter()]
    [string] $Project = (Join-Path $PSScriptRoot "..\SentryApp\SentryApp.csproj"),

    [Parameter()]
    [string] $Destination = (Join-Path $PSScriptRoot "..\artifacts\SentryApp-IIS"),

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string] $Configuration = "Release",

    [Parameter()]
    [ValidateRange(0, 60)]
    [int] $ShutdownWaitSeconds = 10
)

$ErrorActionPreference = "Stop"

$projectPath = [System.IO.Path]::GetFullPath($Project)
$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$stagingPath = Join-Path ([System.IO.Path]::GetTempPath()) ("SentryApp-publish-" + [guid]::NewGuid())
$appOfflinePath = Join-Path $destinationPath "app_offline.htm"
$appWasTakenOffline = $false

try {
    # Build away from the IIS site so a running worker cannot lock publish output.
    & dotnet publish $projectPath --configuration $Configuration --output $stagingPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
    if (Test-Path -LiteralPath $appOfflinePath) {
        throw "The destination is already offline: '$appOfflinePath' exists. Remove it when it is safe to deploy."
    }

    # ASP.NET Core Module sees this file, gracefully stops the app, and releases
    # assemblies before they are replaced. Keep it in place for the whole copy.
    $appWasTakenOffline = $true
    Set-Content `
        -LiteralPath $appOfflinePath `
        -Value "<!doctype html><title>SentryApp maintenance</title><h1>SentryApp is being updated.</h1>" `
        -Encoding UTF8

    if ($ShutdownWaitSeconds -gt 0) {
        Start-Sleep -Seconds $ShutdownWaitSeconds
    }

    Get-ChildItem -LiteralPath $stagingPath | Copy-Item `
        -Destination $destinationPath `
        -Recurse `
        -Force
}
finally {
    if ($appWasTakenOffline -and (Test-Path -LiteralPath $appOfflinePath)) {
        Remove-Item -LiteralPath $appOfflinePath -Force
    }

    if (Test-Path -LiteralPath $stagingPath) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}
