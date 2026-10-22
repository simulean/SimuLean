# Runs every standalone (headless, no Unity) suite: the reference models against their
# baselines, then the unit suites. Exit code 1 if any suite fails.
#   powershell -ExecutionPolicy Bypass -File StandaloneTests/run-all.ps1 [-Serial] [suite arguments]
# The suites run in parallel (each compiles the engine sources in its own project, so their builds do not
# share files; 2026-10-08), their output printed in suite order; -Serial runs them one by one.
# The EditMode layer runs in Unity (batch mode, the editor must be closed):
#   Unity.exe -batchmode -runTests -projectPath . -testPlatform EditMode -testResults results.xml
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$suites = @('referencemodels', 'bugfixtests', 'stackertests', 'sourcestatstests', 'listtests', 'seedtests', 'resourcetests', 'propertytests', 'expressiontests', 'optimizationtests', 'probabilitytests', 'componenttests', 'fittingtests', 'resultstests', 'navaltests', 'datatests', 'signaltests', 'agentinterfacetests', 'docsymboltests', 'architecturelint')
$serial = $args -contains '-Serial'
$suiteArgs = @($args | Where-Object { $_ -ne '-Serial' })
$failed = @()

if ($serial) {
    foreach ($s in $suites) {
        Write-Host "== $s" -ForegroundColor Cyan
        dotnet run --project (Join-Path $root "$s/$s.csproj") -v quiet --nologo -- @suiteArgs
        if ($LASTEXITCODE -ne 0) { $failed += $s }
    }
} else {
    $out = Join-Path ([IO.Path]::GetTempPath()) ("simulean-run-all-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $out | Out-Null
    $quoted = @($suiteArgs | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
    $procs = @{}
    try {
        foreach ($s in $suites) {
            $argLine = "run --project `"$(Join-Path $root "$s/$s.csproj")`" -v quiet --nologo -- $quoted"
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
# (Tools/Docs/command_reference.py; 2026-10-20). Needs Python; skipped without it.
Write-Host '== commandreference' -ForegroundColor Cyan
if (Get-Command python -ErrorAction SilentlyContinue) {
    python (Join-Path $root '..\Tools\Docs\command_reference.py') --check
    if ($LASTEXITCODE -ne 0) { $failed += 'commandreference' }
} else { Write-Host '  skipped: no python on the PATH' }
if ($failed.Count -gt 0) { Write-Host "FAILED: $($failed -join ', ')" -ForegroundColor Red; exit 1 }
Write-Host "All suites passed." -ForegroundColor Green
