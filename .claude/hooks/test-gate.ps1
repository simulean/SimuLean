# Test gate. Two callers:
#
#   post      - Claude Code hook after every edit (Edit / Write / Bash / PowerShell; .claude/settings.json):
#               if the sources the standalone tests compile changed since the last run, run
#               StandaloneTests/run-all.ps1, and if any C# under Assets changed, compile-check the Unity
#               code without the editor (StandaloneTests/unity-compile-check.ps1); on failure exit 2 so
#               the failure is reported back immediately (a broken bulk-edit script is caught at once).
#   precommit - git pre-commit hook (.githooks/pre-commit; every commit: console, GitHub Desktop,
#               Claude). Fails the commit (exit 1) unless
#               1. the standalone tests pass, the Unity code compiles, and
#               2. the Unity EditMode tests pass:
#                  a. a FULL run in the open editor (SimuLeanTestRunner.Start(""), results in
#                     Temp/simulean_test_results_full.json) completed with no failure and no skipped test counts
#                     when the working-tree fingerprint it recorded at its start (this script's `treefp` mode) still
#                     matches, i.e. nothing it covers changed, was deleted or added since - a
#                     full run with failures blocks the commit;
#                  b. else they run in batch mode (Unity.exe -runTests), which needs the editor CLOSED
#                     (Unity locks the project). With the editor open the commit goes ahead with a warning
#                     and the tests stay pending: they run on the first commit made with the editor closed
#                     (CLAUDE.md: the agent asks the user to close Unity, or leaves them pending).
#                  SIMULEAN_SKIP_UNITY_TESTS=1 skips step 2.
#               The tests see the working tree: when code (.cs / .asmdef, the baselines, the package manifest)
#               has unstaged changes or untracked scripts, a pass is not cached for the index.
#   treefp    - prints the working-tree fingerprint SimuLeanTestRunner records when a full run starts.
#
# A fingerprint avoids re-running a battery when nothing changed; the last passing / failing fingerprints
# live in .claude/hooks/.state (git-ignored). Standalone and the compile check fingerprint the files (path + size
# + write time) of Assets/SimuLean.Net and StandaloneTests / of the C# under Assets. The Unity EditMode verdict
# fingerprints the git INDEX (what is committed: scripts, scenes, prefabs and assets under Assets, the package
# manifest and the baselines), so the user's unstaged work in progress does not invalidate it (2026-10-08).
# Unity editor: $env:UNITY_EDITOR_PATH, else the Hub install of ProjectSettings/ProjectVersion.txt.
param([ValidateSet('post', 'precommit', 'treefp')][string]$Mode = 'post')

$ErrorActionPreference = 'Stop'
# Any unexpected failure of the gate itself is reported (and fails the hook), never silently ignored.
trap { [Console]::Error.WriteLine("test-gate hook error: $_"); exit 2 }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

# What the Unity EditMode verdict covers.
$uPaths = @('Assets', 'Packages\manifest.json', 'ProjectSettings\ProjectVersion.txt', 'StandaloneTests\referencemodels\baselines')
$uExts = @('.cs', '.unity', '.prefab', '.asset', '.asmdef', '.json', '.txt')

function Get-Fingerprint([string[]]$paths, [string[]]$exts) {
    $sb = New-Object System.Text.StringBuilder
    foreach ($p in $paths) {
        $full = Join-Path $root $p
        if (-not (Test-Path $full)) { continue }
        Get-ChildItem -Path $full -Recurse -File |
            Where-Object { $exts -contains $_.Extension -and $_.FullName -notmatch '\\(bin|obj)\\' } |
            Sort-Object FullName |
            ForEach-Object { [void]$sb.Append("$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)`n") }
    }
    $sha = [System.Security.Cryptography.SHA1]::Create()
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($sb.ToString()))
    return ([System.BitConverter]::ToString($bytes) -replace '-', '')
}

# Fingerprint of the WORKING TREE of the files the EditMode verdict covers (path + size + write time of each, tracked
# or not): any edit, deletion, rename or new file changes it, staging does not.
function Get-TreeFingerprint { return Get-Fingerprint $uPaths $uExts }

if ($Mode -eq 'treefp') { Write-Output (Get-TreeFingerprint); exit 0 }

# Code the tests compile or compare with that differs from the index (unstaged edits, untracked scripts): a verdict
# on the working tree is then not a verdict on what is committed.
function Get-UnstagedCode {
    $code = @('Assets/*.cs', 'Assets/*.asmdef', 'StandaloneTests/referencemodels/baselines', 'Packages/manifest.json')
    return @(@(& git -C $root diff --name-only -- $code) + @(& git -C $root ls-files -o --exclude-standard -- 'Assets/*.cs' 'Assets/*.asmdef') |
             Where-Object { $_ } | Sort-Object -Unique)
}

# One gate at a time per checkout: parallel tool calls (or sessions) would otherwise build the same test
# projects concurrently and fail on locked files. Later callers wait, then usually hit the cache.
$mutexName = 'Global\SimuLeanTestGate_' + (($root.ToLowerInvariant() -replace '[^a-z0-9]', '_'))
$mutex = New-Object System.Threading.Mutex($false, $mutexName)
# Bounded wait: a hung holder (a stuck batch Unity) must not block every later hook of every session.
$owned = $false
try { $owned = $mutex.WaitOne([TimeSpan]::FromMinutes(20)) } catch [System.Threading.AbandonedMutexException] { $owned = $true }   # previous holder died: we own it now
if (-not $owned) {
    [Console]::Error.WriteLine('test-gate: another gate run has held the lock for 20 minutes (a hung batch Unity?) - giving up.')
    exit 2
}
$stateDir = Join-Path $PSScriptRoot '.state'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null

if ($Mode -eq 'post') { [void][Console]::In.ReadToEnd() }   # the hook's JSON event (not needed)

# Fingerprint of the staged (index) versions of the tracked files under $paths with an extension in $exts.
function Get-IndexFingerprint([string[]]$paths, [string[]]$exts) {
    $lines = @(& git -C $root ls-files -s -- @($paths | ForEach-Object { $_ -replace '\\', '/' }) |
               Where-Object { $exts -contains [IO.Path]::GetExtension(($_ -split "`t", 2)[1]) })
    $sha = [System.Security.Cryptography.SHA1]::Create()
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
    return ([System.BitConverter]::ToString($bytes) -replace '-', '')
}

# The last FULL run of SimuLeanTestRunner in the open editor, if it completed (else $null).
function Get-EditorRun {
    $path = Join-Path $root 'Temp\simulean_test_results_full.json'
    if (-not (Test-Path $path)) { return $null }
    $text = Get-Content $path -Raw
    if ($text -notmatch '"status":"completed"' -or $text -notmatch '"full":true') { return $null }
    $num = { param($k) if ($text -match ('"' + $k + '":(\d+)')) { [long]$Matches[1] } else { -1 } }
    $tree = if ($text -match '"treeFp":"([0-9A-F]*)"') { $Matches[1] } else { '' }
    return @{ Passed = (& $num 'passed'); Failed = (& $num 'failed'); Skipped = (& $num 'skipped'); TreeFp = $tree; Text = $text }
}

# Cached verdict for a fingerprint: 'pass', 'fail' (+ summary lines) or $null (never run).
function Get-Cached([string]$name, [string]$fp) {
    $pass = Join-Path $stateDir "$name-pass.txt"
    $fail = Join-Path $stateDir "$name-fail.txt"
    if ((Test-Path $pass) -and ((Get-Content $pass -Raw).Trim() -eq $fp)) { return @{ ok = $true } }
    if (Test-Path $fail) {
        $c = @(Get-Content $fail)
        if ($c.Count -gt 0 -and $c[0].Trim() -eq $fp) { return @{ ok = $false; summary = @($c | Select-Object -Skip 1) } }
    }
    return $null
}

function Set-Cached([string]$name, [string]$fp, [bool]$ok, [string[]]$summary) {
    $pass = Join-Path $stateDir "$name-pass.txt"
    $fail = Join-Path $stateDir "$name-fail.txt"
    if ($ok) {
        Set-Content -Path $pass -Value $fp -Encoding ascii
        if (Test-Path $fail) { Remove-Item $fail -Force }
    } else {
        Set-Content -Path $fail -Value (@($fp) + $summary) -Encoding utf8
    }
}

# post: exit 2 feeds stderr back to Claude; precommit: any non-zero exit aborts the commit.
function Fail([string]$message, [string[]]$summary) {
    [Console]::Error.WriteLine("$message`n" + ($summary -join "`n"))
    if ($Mode -eq 'post') { exit 2 } else { exit 1 }
}

# ── 1. standalone battery ────────────────────────────────────────────────────

$saPaths = @('Assets\SimuLean.Net', 'StandaloneTests')
$saExts = @('.cs', '.csproj', '.props', '.json', '.ps1')
$saFp = Get-Fingerprint $saPaths $saExts
$cached = Get-Cached 'standalone' $saFp
$blocked = if ($Mode -eq 'precommit') { 'Commit blocked: ' } else { '' }

if ($null -eq $cached) {
    $runAll = Join-Path $root 'StandaloneTests\run-all.ps1'
    # Windows PowerShell 5.1 turns a native command's stderr into errors: do not stop on them.
    $ErrorActionPreference = 'Continue'
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $runAll 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $summary = @()
    if ($code -ne 0) {
        $summary = @($out | Where-Object { $_ -match 'FAIL|EXCEPTION|error|Error|^== ' -and $_ -notmatch ', 0 failed$|RemoteException' } |
                     Select-Object -First 60)
        if ($summary.Count -eq 0) { $summary = @($out | Select-Object -Last 40) }
    }
    Set-Cached 'standalone' $saFp ($code -eq 0) $summary
    if ($code -ne 0) { Fail "${blocked}standalone tests FAILED (StandaloneTests/run-all.ps1):" $summary }
    if ($Mode -eq 'post') { Write-Output 'Standalone tests passed.' }
} elseif (-not $cached.ok) {
    $what = if ($Mode -eq 'precommit') { 'Commit blocked: the standalone tests fail' } else { 'Standalone tests still failing (sources unchanged since the failing run)' }
    Fail $what $cached.summary
}

# ── 2. Unity code compiles (no editor needed: StandaloneTests/unity-compile-check.ps1) ──

$ccPaths = @('Assets', 'Assembly-CSharp.csproj', 'Assembly-CSharp-Editor.csproj', 'StandaloneTests\unity-compile-check.ps1')
$ccFp = Get-Fingerprint $ccPaths @('.cs', '.asmdef', '.csproj', '.ps1')
$cached = Get-Cached 'unitycompile' $ccFp
if ($null -eq $cached) {
    $ErrorActionPreference = 'Continue'
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'StandaloneTests\unity-compile-check.ps1') 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $summary = if ($code -ne 0) { @($out | Where-Object { $_ -notmatch '^== ' } | Select-Object -First 40) } else { @() }
    Set-Cached 'unitycompile' $ccFp ($code -eq 0) $summary
    if ($code -ne 0) { Fail "${blocked}Unity code does NOT compile (StandaloneTests/unity-compile-check.ps1):" $summary }
} elseif (-not $cached.ok) {
    $what = if ($Mode -eq 'precommit') { 'Commit blocked: the Unity code does not compile' } else { 'Unity code still does not compile (sources unchanged since the failing check)' }
    Fail $what $cached.summary
}
if ($Mode -eq 'post') { exit 0 }

# ── 3. Unity EditMode tests (pre-commit only, batch mode) ────────────────────

if ($env:SIMULEAN_SKIP_UNITY_TESTS -eq '1') {
    Write-Output 'Unity EditMode tests skipped (SIMULEAN_SKIP_UNITY_TESTS=1).'
    exit 0
}

$uFp = Get-IndexFingerprint $uPaths $uExts
$cached = Get-Cached 'unity' $uFp
if ($null -ne $cached -and $cached.ok) { exit 0 }
$unstaged = Get-UnstagedCode
$unstagedNote = ''
if ($unstaged.Count -gt 0) {
    $more = if ($unstaged.Count -gt 5) { ', ...' } else { '' }
    $unstagedNote = 'the tests saw code that is not staged (' + (($unstaged | Select-Object -First 5) -join ', ') + $more +
                    '): the verdict is not kept for this commit'
}

# a. A full run in the open editor on exactly this working tree stands for the batch run. It is checked before a cached
# failure, so a newer passing run wins over an old batch failure.
$editorRun = Get-EditorRun
if ($null -ne $editorRun -and $editorRun.TreeFp -and $editorRun.TreeFp -eq (Get-TreeFingerprint)) {
    if ($editorRun.Failed -eq 0 -and $editorRun.Skipped -eq 0 -and $editorRun.Passed -gt 0) {
        if ($unstagedNote) {
            [Console]::Error.WriteLine("WARNING: the Unity EditMode tests passed in the open editor, but $unstagedNote. Commit allowed.")
        } else {
            Set-Cached 'unity' $uFp $true @()
            Write-Output "Unity EditMode tests: the full run in the open editor passed ($($editorRun.Passed) tests, nothing changed since it started)."
        }
        exit 0
    }
    if ($editorRun.Failed -eq 0) {
        Fail "Commit blocked: the full EditMode run in the open editor skipped $($editorRun.Skipped) test(s); a skipped test is not a pass." @()
    }
    $failures = @(([regex]::Matches($editorRun.Text, '"failures":\[(.*)\]')) | ForEach-Object { $_.Groups[1].Value })
    Fail "Commit blocked: the full EditMode run in the open editor has $($editorRun.Failed) failure(s):" $failures
}
if ($null -ne $cached) {
    Fail 'Commit blocked: the Unity EditMode tests fail (sources unchanged since the failing run):' $cached.summary
}

# The editor locks Temp/UnityLockfile while the project is open.
$lock = Join-Path $root 'Temp\UnityLockfile'
$editorOpen = $false
if (Test-Path $lock) {
    try { $fs = [System.IO.File]::Open($lock, 'Open', 'ReadWrite', 'None'); $fs.Close() } catch { $editorOpen = $true }
}
if ($editorOpen) {
    # Not blocking: the editor is usually open while working. The tests stay pending (no cached pass)
    # and run on the first commit made with the editor closed.
    [Console]::Error.WriteLine('WARNING: Unity EditMode tests PENDING - the sources changed since the last passing run and ' +
        'the editor has this project open (no batch run). Commit allowed. Either run them all in the editor ' +
        '(SimuLeanTestRunner.Start("") then commit again) or close Unity and run: ' +
        'powershell -File .claude/hooks/test-gate.ps1 precommit')
    exit 0
}

# A linked git worktree (".git" is a file) has no imported Library: a batch run would re-import the whole
# project and resolve git packages there. The EditMode tests run in the main checkout instead.
if (Test-Path (Join-Path $root '.git') -PathType Leaf) {
    [Console]::Error.WriteLine('WARNING: linked git worktree - the Unity EditMode tests are not run here (no imported ' +
        'Library). Commit allowed; run them in the main checkout after merging.')
    exit 0
}

# Git runs hooks with GIT_DIR / GIT_INDEX_FILE / ... set. Unity's package manager runs git itself (git
# packages): inheriting them made it write into THIS repository (core.bare = true, 2026-10-06). Unity must
# not see any GIT_* variable.
foreach ($v in @([Environment]::GetEnvironmentVariables('Process').Keys | Where-Object { "$_" -like 'GIT_*' })) {
    [Environment]::SetEnvironmentVariable("$v", $null, 'Process')
}

$unity = $env:UNITY_EDITOR_PATH
if (-not $unity) {
    $ver = (Select-String -Path (Join-Path $root 'ProjectSettings\ProjectVersion.txt') -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
    $unity = "C:\Program Files\Unity\Hub\Editor\$ver\Editor\Unity.exe"
}
if (-not (Test-Path $unity)) {
    Fail "Commit blocked: Unity editor not found at '$unity' (set UNITY_EDITOR_PATH)." @()
}

$results = Join-Path $stateDir 'editmode-results.xml'
$log = Join-Path $stateDir 'editmode.log'
if (Test-Path $results) { Remove-Item $results -Force }
$p = Start-Process -FilePath $unity -PassThru -WindowStyle Hidden -ArgumentList @(
    '-batchmode', '-nographics', '-projectPath', "`"$root`"", '-runTests', '-testPlatform', 'EditMode',
    '-testResults', "`"$results`"", '-logFile', "`"$log`"")
# Bounded: a hung import must not hold the commit and the gate lock forever.
if (-not $p.WaitForExit(45 * 60 * 1000)) {
    try { $p.Kill() } catch { }
    Fail "Commit blocked: the batch Unity run did not finish in 45 minutes (killed). Log: $log" @()
}
# $uFp stays the fingerprint read BEFORE the run: what was staged meanwhile was not tested.

$summary = @()
$ok = $false
if (Test-Path $results) {
    [xml]$x = Get-Content $results -Raw
    $run = $x.'test-run'
    $ok = ($p.ExitCode -eq 0) -and ([string]$run.result -like 'Passed*')
    if (-not $ok) {
        $summary = @("EditMode: $($run.passed) passed, $($run.failed) failed of $($run.total) (exit $($p.ExitCode))")
        foreach ($tc in $x.SelectNodes("//test-case[@result='Failed']")) {
            $msg = [string]$tc.failure.message
            $summary += "  FAIL $($tc.fullname): $($msg.Trim() -split "`n" | Select-Object -First 3)"
        }
    }
} else {
    $summary = @("Unity exited with code $($p.ExitCode) without test results (compile error, licence, crash?). Log: $log")
    if (Test-Path $log) {
        $summary += @(Select-String -Path $log -Pattern 'error CS\d+|Scripts have compiler errors|Aborting batchmode' |
                      Select-Object -First 30 | ForEach-Object { '  ' + $_.Line.Trim() })
    }
    # no results, no verdict: not cached, so a later run (batch or editor) decides
    Fail 'Commit blocked: the Unity EditMode tests did not run:' $summary
}
if ($ok -and $unstagedNote) {
    [Console]::Error.WriteLine("WARNING: the Unity EditMode tests passed, but $unstagedNote. Commit allowed.")
    exit 0
}
Set-Cached 'unity' $uFp $ok $summary
if (-not $ok) { Fail 'Commit blocked: the Unity EditMode tests FAILED:' $summary }
Write-Output 'Standalone and Unity EditMode tests passed.'
exit 0
