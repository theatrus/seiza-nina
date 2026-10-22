[CmdletBinding()]
param(
    [switch]$Test,
    [string]$NinaSourcePath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$upstream = Get-Content -LiteralPath (Join-Path $repoRoot 'upstream.json') -Raw | ConvertFrom-Json

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

Push-Location $repoRoot
try {
    if (-not $NinaSourcePath) {
        $NinaSourcePath = Join-Path $repoRoot '.deps/nina'
        if (-not (Test-Path -LiteralPath (Join-Path $NinaSourcePath '.git'))) {
            New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot '.deps') | Out-Null
            Invoke-Checked 'git' @('clone', '--depth', '1', '--branch', 'develop', $upstream.repository, $NinaSourcePath)
            Invoke-Checked 'git' @('-C', $NinaSourcePath, 'fetch', '--depth', '1', 'origin', $upstream.ref)
            Invoke-Checked 'git' @('-C', $NinaSourcePath, 'checkout', '--detach', $upstream.ref)
        }
        $actual = & git -C $NinaSourcePath rev-parse HEAD
        if ($LASTEXITCODE -ne 0 -or $actual -ne $upstream.ref) {
            throw 'The default .deps/nina checkout differs from upstream.json. Use -NinaSourcePath to explicitly build against another checkout.'
        }
    }

    $NinaSourcePath = (Resolve-Path -LiteralPath $NinaSourcePath).Path
    if (-not (Test-Path -LiteralPath (Join-Path $NinaSourcePath 'NINA.Platesolving/Interfaces/IPlateSolverProvider.cs'))) {
        throw 'This NINA checkout does not include the plugin plate solver API from PR #449.'
    }

    Invoke-Checked 'cargo' @('build', '--release', '--locked')
    $buildProperties = @('-c', 'Release', '-p:GeneratePackageOnBuild=false', "-p:NinaSourcePath=$NinaSourcePath")
    Invoke-Checked 'dotnet' (@('build', 'src/Seiza.Nina/Seiza.Nina.csproj') + $buildProperties)
    if ($Test) {
        Invoke-Checked 'cargo' @('test', '--release', '--locked')
        Invoke-Checked 'cargo' @('run', '--release', '--locked', '--example', 'make-fixture', '--', 'artifacts/test-fixture')
        Invoke-Checked 'dotnet' (@('test', 'tests/Seiza.Nina.Tests/Seiza.Nina.Tests.csproj', '--logger', 'trx;LogFileName=seiza-nina.trx', '--results-directory', 'artifacts/test-results') + $buildProperties)
    }

    $output = Join-Path $repoRoot 'src/Seiza.Nina/bin/Release/net10.0-windows'
    $stage = Join-Path $repoRoot 'artifacts/plugin'
    New-Item -ItemType Directory -Force -Path $stage | Out-Null
    # Explicit file list: NINA supplies all managed dependencies.
    foreach ($name in @('Seiza.Nina.dll', 'seiza_nina_native.dll')) {
        Copy-Item -LiteralPath (Join-Path $output $name) -Destination $stage -Force
    }
    foreach ($name in @('README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination $stage -Force
    }
    # Include upstream license/notice texts from the exact locked crate sources.
    $metadataText = & cargo metadata --locked --filter-platform x86_64-pc-windows-msvc --format-version 1
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve dependency license sources.' }
    $metadata = $metadataText | ConvertFrom-Json
    $resolvedIds = @($metadata.resolve.nodes.id)
    $licenseText = [System.Text.StringBuilder]::new()
    foreach ($package in ($metadata.packages | Sort-Object name, version)) {
        if ($package.id -notin $resolvedIds) { continue }
        if ($package.name -eq 'seiza-nina-native') { continue }
        [void]$licenseText.AppendLine("===== $($package.name) $($package.version): $($package.license) =====")
        [void]$licenseText.AppendLine("Source: $($package.repository)")
        $source = Split-Path -Parent $package.manifest_path
        $notices = @(Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|COPYRIGHT|NOTICE|UNLICENSE)(\.|-|$)' })
        foreach ($notice in $notices) {
            [void]$licenseText.AppendLine("--- $($notice.Name) ---")
            [void]$licenseText.AppendLine((Get-Content -LiteralPath $notice.FullName -Raw))
        }
        if ($notices.Count -eq 0) {
            if ($package.name -eq 'seiza' -or $package.name.StartsWith('seiza-')) {
                # The first-party published crates declare Apache-2.0 but omit the
                # workspace's license text. Supply the same license from this repo.
                [void]$licenseText.AppendLine((Get-Content -LiteralPath (Join-Path $repoRoot 'LICENSE') -Raw))
            }
            else { throw "No license text found for $($package.name); inspect the crate before packaging." }
        }
    }
    $licenseText.ToString() | Set-Content -LiteralPath (Join-Path $stage 'THIRD-PARTY-LICENSES.txt') -Encoding utf8NoBOM
    $version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'Seiza.Nina.dll')).FileVersion
    $zipName = "seiza-nina-$version-win-x64.zip"
    $archive = Join-Path $repoRoot "artifacts/$zipName"
    $files = @('Seiza.Nina.dll', 'seiza_nina_native.dll', 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-LICENSES.txt') | ForEach-Object { Join-Path $stage $_ }
    Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
    $checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$checksum  $zipName" | Set-Content -LiteralPath (Join-Path $repoRoot 'artifacts/SHA256SUMS.txt') -Encoding utf8NoBOM
    Write-Host "Plugin package: $archive"
}
finally {
    Pop-Location
}
