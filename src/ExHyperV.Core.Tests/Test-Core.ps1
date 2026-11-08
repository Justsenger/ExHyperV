#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Regression', 'Windows', 'All')]
    [string]$Suite = 'Regression',
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = $(if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }),
    [string]$Filter,
    [switch]$NoCoverage,
    [switch]$LockedRestore
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$projectDirectory = $PSScriptRoot
$artifactRoot = Join-Path $repo '.artifacts/1x/core-tests'
$buildRoot = Join-Path $artifactRoot "build/$Runtime"
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$results = Join-Path $artifactRoot "results/$Runtime/$runId"
$null = New-Item -ItemType Directory -Path $results -Force

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio 2026 / Build Tools with the .NET desktop workload (Core has a COM reference).' }
$msbuild = @(& $vswhere -latest -products '*' -version '[18.0,19.0)' -requires Microsoft.Component.MSBuild -find 'MSBuild\Current\Bin\MSBuild.exe') | Select-Object -First 1
if (!$msbuild) { throw 'Visual Studio 2026 MSBuild was not found. dotnet build cannot resolve the Core IMAPI2FS COM reference.' }
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

# Check the maintained test plan against the current public API before building.
& $dotnet run --project (Join-Path $PSScriptRoot 'Tools/Catalog') -- --audit-tests $repo
if ($LASTEXITCODE -ne 0) { throw '公开 API 缺少测试计划，请更新 TestPlanning/TestCatalog.json。' }

$environmentBefore = @{}
foreach ($name in @('EXHYPERV_TEST_WINDOWS', 'TESTINGPLATFORM_TELEMETRY_OPTOUT', 'DOTNET_CLI_TELEMETRY_OPTOUT')) {
    $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    $env:TESTINGPLATFORM_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:EXHYPERV_TEST_WINDOWS = if ($Suite -eq 'Regression') { '0' } else { '1' }
    Push-Location $projectDirectory
    try {
        # VS MSBuild handles COM; isolated artifacts avoid the application build's obj/bin.
        $buildArguments = @(
            'ExHyperV.Core.Tests.csproj', '/restore', '/t:Build', '/nologo', '/nr:false',
            '/p:Configuration=Release', "/p:RuntimeIdentifier=$Runtime",
            '/p:UseArtifactsOutput=true', "/p:ArtifactsPath=$buildRoot",
            '/clp:ErrorsOnly;Summary', "/flp:LogFile=$results/build.log;Verbosity=normal"
        )
        if ($LockedRestore) { $buildArguments += '/p:RestoreLockedMode=true' }
        & $msbuild @buildArguments
        if ($LASTEXITCODE -ne 0) { throw "Core test build failed ($LASTEXITCODE). Log: $results/build.log" }

        $module = Join-Path $buildRoot "bin/ExHyperV.Core.Tests/release_$Runtime/ExHyperV.Core.Tests.exe"
        if (!(Test-Path -LiteralPath $module)) { throw "Test module was not built: $module" }
        $selection = switch ($Suite) {
            'Regression' { 'TestCategory=Regression' }
            'Windows' { 'TestCategory=WindowsIntegration' }
            'All' { '(TestCategory=Regression|TestCategory=WindowsIntegration)' }
        }
        if ($Filter) { $selection = "($selection)&($Filter)" }
        $testArguments = @(
            'test', '--test-modules', (Split-Path $module -Leaf), '--root-directory', (Split-Path $module -Parent), '--filter', $selection,
            '--results-directory', $results, '--report-trx', '--report-trx-filename', 'tests.trx',
            '--minimum-expected-tests', '1', '--zero-tests-policy', 'strict', '--timeout', '3m',
            '--no-ansi', '--progress', 'off'
        )
        if (!$NoCoverage) {
            $testArguments += @('--coverlet', '--coverlet-output-format', 'cobertura',
                '--coverlet-include', '[ExHyperV.Core]*',
                '--coverlet-exclude-by-file', '**/Resources.Designer.cs',
                '--coverlet-exclude-by-attribute', 'GeneratedCodeAttribute')
        }
        & $dotnet @testArguments 2>&1 | Tee-Object -FilePath (Join-Path $results 'test.log')
        $testExitCode = $LASTEXITCODE

        $summary = [ordered]@{ suite = $Suite; runtime = $Runtime; filter = $selection; runnerExitCode = $testExitCode; results = $results }
        $trx = Join-Path $results 'tests.trx'
        if (Test-Path -LiteralPath $trx) {
            [xml]$report = Get-Content -LiteralPath $trx -Raw
            $counts = $report.TestRun.ResultSummary.Counters
            $summary.total = [int]$counts.total
            $summary.passed = [int]$counts.passed
            $summary.failed = [int]$counts.failed
            $summary.notExecuted = [int]$counts.notExecuted
        }
        $coverage = @(Get-ChildItem -LiteralPath $results -Filter 'coverage.cobertura.*.xml')
        if ($coverage.Count -eq 1) {
            [xml]$coverageReport = Get-Content -LiteralPath $coverage[0].FullName -Raw
            $summary.lineCoverage = [double]::Parse($coverageReport.coverage.'line-rate', [Globalization.CultureInfo]::InvariantCulture)
            $summary.branchCoverage = [double]::Parse($coverageReport.coverage.'branch-rate', [Globalization.CultureInfo]::InvariantCulture)
        }
        $problems = @()
        if ($testExitCode -ne 0) { $problems += "Core tests failed (runner exit code $testExitCode)." }
        if (!(Test-Path -LiteralPath $trx)) { $problems += 'The runner did not produce the required TRX report.' }
        elseif ([int]$counts.notExecuted -gt 0) { $problems += 'The selected suite contains unexecuted tests.' }
        if (!$NoCoverage) {
            if ($coverage.Count -ne 1) { $problems += 'The runner did not produce exactly one Core coverage report.' }
            elseif (!($coverageReport.coverage.packages.package | Where-Object name -eq 'ExHyperV.Core') -or
                [int]$coverageReport.coverage.'lines-valid' -le 0) {
                $problems += 'Coverage did not instrument Core code; an empty report is not a valid result.'
            }
        }
        $summary.status = if ($problems.Count -eq 0) { 'passed' } else { 'failed' }
        $summary.problems = $problems
        $summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results 'summary.json') -Encoding utf8
        Write-Host "Reports: $results"
        if ($problems.Count -gt 0) { throw ($problems -join ' ') }
    }
    finally { Pop-Location }
}
finally {
    foreach ($name in $environmentBefore.Keys) {
        [Environment]::SetEnvironmentVariable($name, $environmentBefore[$name], 'Process')
    }
}
