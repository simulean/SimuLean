# Runs every standalone (headless, no Unity) suite: the reference models against their
# baselines, then the unit suites. Exit code 1 if any suite fails.
#   powershell -ExecutionPolicy Bypass -File StandaloneTests/run-all.ps1 [-Serial] [suite arguments]
# One build first: StandaloneTests.proj builds the engine library once (Engine/Engine.csproj, referenced by every suite
# through Tests.props) and the suites in parallel, in a single MSBuild run;
# then the built suites run in parallel, their output printed in suite order; -Serial runs them
# one by one.
# The EditMode layer runs in Unity (batch mode, the editor must be closed):
#   Unity.exe -batchmode -runTests -projectPath . -testPlatform EditMode -testResults results.xml
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$suites = @('referencemodels', 'bugfixtests', 'stackertests', 'sourcestatstests', 'listtests', 'seedtests', 'resourcetests', 'propertytests', 'expressiontests', 'optimizationtests', 'probabilitytests', 'componenttests', 'fittingtests', 'resultstests', 'navaltests', 'datatests', 'signaltests', 'warehousetests', 'linesupplytests', 'milkruntests', 'sequencingtests', 'setuptests', 'preemptiontests', 'primitivetests', 'choicetests', 'logictests', 'maxwaittests', 'agentinterfacetests', 'runnertests', 'extractiontests', 'docsymboltests', 'architecturelint')
$serial = $args -contains '-Serial'
$suiteArgs = @($args | Where-Object { $_ -ne '-Serial' })
$failed = @()

# Every suite folder is listed above (perfbench is a benchmark, built but not run; Engine is the library).
$folders = @(Get-ChildItem -Path $root -Directory | Where-Object { Test-Path (Join-Path $_.FullName "$($_.Name).csproj") } |
             ForEach-Object { $_.Name } | Where-Object { $_ -notin @('perfbench', 'Engine') })
$unlisted = @($folders | Where-Object { $_ -notin $suites })
if ($unlisted.Count -gt 0) { Write-Host "run-all.ps1: suite(s) not in its list: $($unlisted -join ', ')" -ForegroundColor Red; exit 1 }

Write-Host '== build' -ForegroundColor Cyan
$ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns a native command's stderr into errors
$build = & dotnet build (Join-Path $root 'StandaloneTests.proj') -m -v quiet --nologo -clp:NoSummary 2>&1 | ForEach-Object { "$_" }
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
if ($code -ne 0) {
    $build | Where-Object { $_ -match 'error' } | Select-Object -Unique | Write-Host
    Write-Host "FAILED: build (dotnet build StandaloneTests/StandaloneTests.proj, exit $code)" -ForegroundColor Red
    exit 1
}

function Get-SuiteDll([string]$s) { return Join-Path $root "$s/bin/Debug/net10.0/$s.dll" }

if ($serial) {
    foreach ($s in $suites) {
        Write-Host "== $s" -ForegroundColor Cyan
        dotnet (Get-SuiteDll $s) @suiteArgs
        if ($LASTEXITCODE -ne 0) { $failed += $s }
    }
} else {
    $out = Join-Path ([IO.Path]::GetTempPath()) ("simulean-run-all-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $out | Out-Null
    $quoted = @($suiteArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
    $procs = @{}
    try {
        foreach ($s in $suites) {
            $argLine = "`"$(Get-SuiteDll $s)`" $quoted"
            $procs[$s] = Start-Process -FilePath 'dotnet' -ArgumentList $argLine -NoNewWindow -PassThru `
                -RedirectStandardOutput (Join-Path $out "$s.out") -RedirectStandardError (Join-Path $out "$s.err")
            [void]$procs[$s].Handle   # Windows PowerShell 5.1: without the handle cached now, ExitCode reads null later
        }
        foreach ($s in $suites) {
            $p = $procs[$s]
            $p.WaitForExit()
            Write-Host "== $s" -ForegroundColor Cyan
            Get-Content (Join-Path $out "$s.out") | Write-Host
            $err = Join-Path $out "$s.err"
            if ((Test-Path $err) -and (Get-Item $err).Length -gt 0) { Get-Content $err | Write-Host }
            if ($p.ExitCode -ne 0) { $failed += $s }
        }
    } finally {
        foreach ($p in $procs.Values) { if (-not $p.HasExited) { $p.Kill() } }
        Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
    }
}
# The agents' command reference (docs/agents/commands.md) is generated from the code: it must be current
# (Tools/Docs/command_reference.py). Needs Python; skipped without it.
Write-Host '== commandreference' -ForegroundColor Cyan
if (Get-Command python -ErrorAction SilentlyContinue) {
    python (Join-Path $root '..\Tools\Docs\command_reference.py') --check
    if ($LASTEXITCODE -ne 0) { $failed += 'commandreference' }
} else { Write-Host '  skipped: no python on the PATH' }
if ($failed.Count -gt 0) { Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host "All suites passed." -ForegroundColor Green
