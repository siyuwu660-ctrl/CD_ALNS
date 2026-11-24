<#
.SYNOPSIS
Build the frozen CD-ALNS solver and run the nine seed-zero experiments on Windows x64.
.PARAMETER DataRoot
Directory containing SMALL_5S_30T_ONEWAY_V3, JZ and JH with their original CSV inputs.
.PARAMETER OutputRoot
Parent directory for a new run. Defaults to runs beside this script.
.PARAMETER CheckOnly
Build and validate each case and method without starting optimization.
.EXAMPLE
.\Run-Main.ps1 -DataRoot 'E:\inputs\data' -CheckOnly
.EXAMPLE
.\Run-Main.ps1 -DataRoot 'E:\inputs\data' -OutputRoot 'E:\CD-ALNS-runs'
.NOTES
Requires a .NET 8 or newer SDK targeting net8.0 and the .NET 8 x64 runtime.
Optimization requires a Gurobi 10.0.1 license. A solver exit code of 2 means
no strictly accepted solution. Each method writes its exit code beside its output.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DataRoot,
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'runs'),
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$inputs = (Resolve-Path -LiteralPath $DataRoot).Path
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'server_config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$project = Join-Path $PSScriptRoot 'source/src/GurobiTimetablingResearch/GurobiTimetablingResearch.csproj'
$null = New-Item -ItemType Directory -Path $OutputRoot -Force
$runPath = Join-Path $OutputRoot ([IO.Path]::GetRandomFileName())
$run = (New-Item -ItemType Directory -Path $runPath).FullName
$binary = Join-Path $run 'bin'
Write-Host "Run directory: $run"
dotnet publish $project -c Release -r win-x64 --self-contained false --artifacts-path (Join-Path $run 'build') -o $binary
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
$solver = Join-Path $binary 'GurobiTimetablingResearch.exe'

foreach ($case in $config.cases) {
    $dataset = Join-Path $inputs ([IO.Path]::GetFileName($case.path))
    foreach ($method in @('alns', 'mip', 'lbbd')) {
        $job = New-Item -ItemType Directory -Path (Join-Path $run "$($case.id)_${method}_guided_seed0")
        Set-Content -LiteralPath (Join-Path $job.FullName 'gurobi.env') -Value 'Threads 4' -Encoding Ascii
        $output = Join-Path $job.FullName 'solver'
        $solverArgs = @('--server-package', "--method=$method", "--dataset=$dataset", "--output=$output",
            "--total=$($case.total)", "--reserve=$($case.reserve)", "--macro=$($case.macro)",
            "--bound=$($case.bound)", '--seed=0', '--quota-mode=guided')
        if ($CheckOnly) { $solverArgs += '--check-only' }
        Push-Location -LiteralPath $job.FullName
        try {
            & $solver @solverArgs
            $exitCode = $LASTEXITCODE
        } finally { Pop-Location }
        Set-Content -LiteralPath (Join-Path $job.FullName 'exit_code.txt') -Value $exitCode -Encoding Ascii
        if ($exitCode -ne 0 -and ($CheckOnly -or $exitCode -ne 2)) {
            throw "$($case.id) $method failed with exit code $exitCode."
        }
        Write-Host "$($case.id) $method exited with $exitCode."
    }
}
