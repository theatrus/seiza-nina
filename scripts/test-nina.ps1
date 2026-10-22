#Requires -Version 7.4
[CmdletBinding()]
param(
    [string]$NinaSourcePath,
    [switch]$InstallCatalogs,
    [switch]$KeepOpen
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $NinaSourcePath) { $NinaSourcePath = Join-Path $repoRoot '.deps/nina' }
$NinaSourcePath = (Resolve-Path -LiteralPath $NinaSourcePath).Path

function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}

Push-Location $repoRoot
try {
    # A unique root prevents collisions with another run or the user's NINA state.
    $runRoot = Join-Path $repoRoot ('artifacts/nina-e2e/' + [guid]::NewGuid().ToString('N'))
    $fixture = Join-Path $runRoot 'catalogs'
    New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
    Invoke-Checked 'cargo' @('build', '--release', '--locked')
    Invoke-Checked 'cargo' @('run', '--release', '--locked', '--example', 'make-fixture', '--', $fixture)
    Invoke-Checked 'dotnet' @('build', 'src/Seiza.Nina/Seiza.Nina.csproj', '-c', 'Release', '-p:GeneratePackageOnBuild=false', "-p:NinaSourcePath=$NinaSourcePath")
    # Building the full app requires NINA's external camera/native SDK assets.
    Invoke-Checked 'dotnet' @('build', 'tests/Seiza.Nina.NinaHost/Seiza.Nina.NinaHost.csproj', '-c', 'Release', '-p:Platform=x64', '-p:GeneratePackageOnBuild=false', "-p:NinaSourcePath=$NinaSourcePath")
    $hostDll = Join-Path $repoRoot 'tests/Seiza.Nina.NinaHost/bin/x64/Release/net10.0-windows/win-x64/Seiza.Nina.NinaHost.dll'
    $environment = @{
        SEIZA_NINA_E2E_ROOT = $runRoot
        SEIZA_NINA_E2E_PLUGIN = Join-Path $repoRoot 'src/Seiza.Nina/bin/Release/net10.0-windows'
        SEIZA_NINA_E2E_FIXTURE = $fixture
        SEIZA_CATALOG_DIR = $fixture
        SEIZA_STAR_DATA = Join-Path $fixture 'stars.bin'
        SEIZA_BLIND_INDEX = Join-Path $fixture 'blind-gaia16.idx'
        SEIZA_NINA_E2E_STAY_OPEN = $(if ($KeepOpen) { '1' } else { '0' })
        SEIZA_NINA_E2E_INSTALL = $(if ($InstallCatalogs) { '1' } else { '0' })
    }
    $process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @('exec', ('"{0}"' -f $hostDll)) -Environment $environment -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $runRoot 'stdout.log') -RedirectStandardError (Join-Path $runRoot 'stderr.log')
    $reportPath = Join-Path $runRoot 'report.json'
    $deadline = [DateTime]::UtcNow.AddMinutes(6)
    while (-not (Test-Path -LiteralPath $reportPath)) {
        $process.Refresh()
        if ($process.HasExited) { throw "NINA exited before producing a report. See $runRoot" }
        if ([DateTime]::UtcNow -gt $deadline) {
            Stop-Process -Id $process.Id
            throw "NINA test timed out. See $runRoot"
        }
        Start-Sleep -Milliseconds 250
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (-not $KeepOpen) {
        if (-not $process.WaitForExit(30000)) {
            Stop-Process -Id $process.Id
            throw "NINA did not shut down after testing. See $runRoot"
        }
        if ($process.ExitCode -ne 0) { throw "NINA test process failed. See $reportPath" }
    }
    if ($report.status -ne 'passed') { throw "NINA end-to-end checks failed. See $reportPath" }
    Write-Host "NINA $($report.ninaVersion): $($report.checks.Count) checks passed. Report and screenshots: $runRoot"
}
finally { Pop-Location }
