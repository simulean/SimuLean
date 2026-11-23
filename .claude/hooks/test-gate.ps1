# Test gate. Two callers:
#
#   post      - Claude Code hook after every edit (Edit / Write / Bash / PowerShell; .claude/settings.json):
#               if the sources the standalone tests compile changed since the last run, run
#               StandaloneTests/run-all.ps1, and if any C# under Assets changed, compile-check the Unity
#               code without the editor (StandaloneTests/unity-compile-check.ps1); on failure exit 2 so
#               the failure is reported back immediately (a broken bulk-edit script is caught at once).
#               Cheap when nothing changed (2026-11-13; it fires on every Bash call, read-only ones too): an edit of a
#               file outside the repository returns at once, the fingerprints are one directory walk (~0.2 s) checked
#               BEFORE the lock, and a failure is reported once per fingerprint (the next calls stay quiet until the
#               sources change: a fix re-runs the battery). The lock is waited for 2 minutes at most, and not at all
#               while a pre-commit run holds it (a batch Unity run can take 45 minutes): the call is skipped, the next
#               one checks again. Hook timeout (.claude/settings.json): 600 s = that wait + a cold build and run.
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
#                     (CLAUDE.md: the agent asks the user to close Unity, or leaves them pending). Before Unity
#                     starts, the external runner of this engine version is built when missing (~40 s; its tests
#                     need it, and built by the editor mid-run they were inconclusive and the editor hung on exit).
#                  SIMULEAN_SKIP_UNITY_TESTS=1 skips step 2.
#               The tests see the working tree: when code (.cs / .asmdef, the baselines, the package manifest)
#               has unstaged changes or untracked scripts, a pass is not cached for the index.
#   treefp    - prints the working-tree fingerprint SimuLeanTestRunner records when a full run starts.
#
# A fingerprint avoids re-running a battery when nothing changed; the last passing / failing fingerprints
# live in .claude/hooks/.state (git-ignored). Standalone and the compile check fingerprint the files (path + size
# + write time; bin / obj skipped) of everything the suites compile or read ($saPaths below, plus any file outside
# StandaloneTests a suite's csproj includes) / of the C# under Assets. The Unity EditMode verdict
# fingerprints the git INDEX (what is committed: scripts, scenes, prefabs and assets under Assets, the package
# manifest and the baselines), so the user's unstaged work in progress does not invalidate it (2026-10-08).
# Unity editor: $env:UNITY_EDITOR_PATH, else the Hub install of ProjectSettings/ProjectVersion.txt.
# Runs under Windows PowerShell 5.1 and under PowerShell 7 (pwsh) on Windows, macOS and Linux (.claude/hooks/test-gate.sh
# picks the one the machine has). Off Windows the Unity compile check is skipped: it rewrites the Windows paths of the
# csproj files Unity generates there.
param([ValidateSet('post', 'precommit', 'treefp')][string]$Mode = 'post')

$ErrorActionPreference = 'Stop'
# Any unexpected failure of the gate itself is reported (and fails the hook), never silently ignored.
trap { [Console]::Error.WriteLine("test-gate hook error: $_"); exit 2 }
$onWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$sep = [string][IO.Path]::DirectorySeparatorChar
# the PowerShell running this script runs the suites' scripts too (powershell.exe on Windows, pwsh elsewhere)
$psExe = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$root = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

# What the Unity EditMode verdict covers.
$uPaths = @('Assets', 'Packages\manifest.json', 'ProjectSettings\ProjectVersion.txt', 'StandaloneTests\referencemodels\baselines')
$uExts = @('.cs', '.unity', '.prefab', '.asset', '.asmdef', '.json', '.txt')

# Path + size + write time of every file under $paths (files or folders, relative to the root) with an extension in
# $exts ('' = none), bin / obj folders (and this hook's .state) skipped, hashed in ordinal path order. One
# DirectoryInfo walk: the enumeration returns size and time with each name (no stat per file) - ~0.2 s for the
# repository's sources, where two
# Get-ChildItem | Sort-Object walks took seconds (2026-11-13).
$script:walked = @{}
$script:walkedDirs = @{}
function Get-Fingerprint([string[]]$paths, [string[]]$exts) {
    $extSet = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($e in $exts) { [void]$extSet.Add($e) }
    $lines = New-Object 'System.Collections.Generic.List[string]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $stack = New-Object 'System.Collections.Generic.Stack[IO.DirectoryInfo]'
    foreach ($p in $paths) {
        $full = [IO.Path]::GetFullPath((Join-Path $root ($p -replace '\\', '/')))   # '/' is a separator on every OS
        if ([IO.File]::Exists($full)) {
            $f = New-Object IO.FileInfo $full
            if ($extSet.Contains($f.Extension) -and $seen.Add($f.FullName)) { $lines.Add("$($f.FullName)|$($f.Length)|$($f.LastWriteTimeUtc.Ticks)") }
        } elseif ([IO.Directory]::Exists($full)) { $stack.Push((New-Object IO.DirectoryInfo $full)) }
    }
    while ($stack.Count -gt 0) {
        $d = $stack.Pop()
        $files = $script:walked[$d.FullName]   # a folder walked by an earlier call of this run (Assets: both fingerprints)
        if ($null -eq $files) { $files = @($d.EnumerateFiles()); $script:walked[$d.FullName] = $files }
        foreach ($f in $files) {
            if ($extSet.Contains($f.Extension) -and $seen.Add($f.FullName)) { $lines.Add("$($f.FullName)|$($f.Length)|$($f.LastWriteTimeUtc.Ticks)") }
        }
        $subs = $script:walkedDirs[$d.FullName]
        if ($null -eq $subs) { $subs = @($d.EnumerateDirectories()); $script:walkedDirs[$d.FullName] = $subs }
        foreach ($s in $subs) { if ($s.Name -ne 'bin' -and $s.Name -ne 'obj' -and $s.Name -ne '.state') { $stack.Push($s) } }
    }
    $lines.Sort([StringComparer]::Ordinal)
    $sha = [System.Security.Cryptography.SHA1]::Create()
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
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

$stateDir = Join-Path $PSScriptRoot '.state'
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null

# post: the hook's JSON event. An edit of a file outside the repository (scratch files, memory) cannot change what the
# gate checks: done. Bash / PowerShell calls cannot be told apart cheaply; the fingerprints decide for them.
if ($Mode -eq 'post') {
    $hookEvent = [Console]::In.ReadToEnd()
    if ($hookEvent -match '"tool_name"\s*:\s*"(Edit|Write|MultiEdit|NotebookEdit)"' -and
        $hookEvent -match '"(?:file_path|notebook_path)"\s*:\s*"((?:[^"\\]|\\.)*)"') {
        $edited = $Matches[1] -replace '\\(["\\/])', '$1'   # JSON escapes in a path: \\ \" \/
        if ([IO.Path]::IsPathRooted($edited) -and
            -not ([IO.Path]::GetFullPath($edited)).StartsWith($root.TrimEnd('\', '/') + $sep, [StringComparison]::OrdinalIgnoreCase)) { exit 0 }
    }
}

# ── fingerprints ─────────────────────────────────────────────────────────────

# What the standalone verdict depends on: what the suites compile (the engine and the choice rules, through
# Engine.props / Tests.props; StandaloneTests) and what they read (architecturelint: every C# file under Assets;
# docsymboltests: the docs, the code and script corpus of Assets / StandaloneTests / Tools / .claude/hooks / .githooks;
# agentinterfacetests: docs/agents; run-all's commands.md check: Tools/Docs and the pipeline commands; its public-export
# check: every exported text but Unity's YAML, which the release check covers). Files outside
# StandaloneTests that a suite's csproj includes or imports (Tools/Runner/RunnerCli.cs, Fingerprint.targets) are added
# from the csproj files themselves, so a new one is covered without editing this list.
$saPaths = @('Assets', 'StandaloneTests', 'Tools', 'docs', '.claude\hooks', '.claude\skills', '.githooks', '.github')
# the root's own files (CLAUDE.md, AGENTS.md, README.md...): docsymboltests checks that every file a doc names exists, so
# deleting or restoring one is an input too (a folder elsewhere is covered only by the folders above)
$saPaths += @([IO.Directory]::GetFiles($root) | ForEach-Object { [IO.Path]::GetFileName($_) })
foreach ($proj in [IO.Directory]::EnumerateFiles((Join-Path $root 'StandaloneTests'), '*.*proj*', 'AllDirectories')) {
    if ($proj -match '[\\/](bin|obj)[\\/]') { continue }
    foreach ($m in [regex]::Matches([IO.File]::ReadAllText($proj), '(?:Include|Project)="([^"*$;]+)"')) {
        $rel = $m.Groups[1].Value
        if ($rel -notmatch '^\.\.') { continue }
        $full = [IO.Path]::GetFullPath((Join-Path (Split-Path $proj) $rel))
        if ($full.StartsWith($root + $sep, [StringComparison]::OrdinalIgnoreCase)) { $saPaths += $full.Substring($root.Length + 1) }
    }
}
$saExts = @('.cs', '.csproj', '.props', '.proj', '.targets', '.json', '.ps1', '.md', '.py', '.txt', '.tsv', '.asmdef', '.sh', '.cff', '.yml', '.yaml', '.html', '')
$saFp = Get-Fingerprint $saPaths $saExts
$ccPaths = @('Assets', 'Assembly-CSharp.csproj', 'Assembly-CSharp-Editor.csproj', 'StandaloneTests\unity-compile-check.ps1')
$ccFp = Get-Fingerprint $ccPaths @('.cs', '.asmdef', '.csproj', '.ps1')

# post: a failure is reported once per fingerprint (2026-11-13). The same "still failing" on every following call,
# read-only ones included, was most of the gate's output; the sources changing (a fix) re-runs the check.
function Test-Reported([string]$name, [string]$fp) {
    $f = Join-Path $stateDir "$name-reported.txt"
    return (Test-Path $f) -and ((Get-Content $f -Raw).Trim() -eq $fp)
}
function Set-Reported([string]$name, [string]$fp) {
    if ($Mode -eq 'post') { Set-Content -Path (Join-Path $stateDir "$name-reported.txt") -Value $fp -Encoding ascii }
}

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
    $path = Join-Path $root 'Temp/simulean_test_results_full.json'
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

# post: nothing to do when both verdicts are known and passing, or failing and already reported - decided before the
# lock, so an ordinary call costs the fingerprints only.
if ($Mode -eq 'post') {
    $quiet = $true
    foreach ($c in @(@('standalone', $saFp), @('unitycompile', $ccFp))) {
        if ($c[0] -eq 'unitycompile' -and -not $onWindows) { continue }
        $v = Get-Cached $c[0] $c[1]
        if ($null -eq $v -or (-not $v.ok -and -not (Test-Reported $c[0] $c[1]))) { $quiet = $false }
    }
    if ($quiet) { exit 0 }
}

# One gate at a time per checkout: parallel tool calls (or sessions) would otherwise build the same test
# projects concurrently and fail on locked files. Later callers wait, then usually hit the cache.
$mutexName = 'Global\SimuLeanTestGate_' + (($root.ToLowerInvariant() -replace '[^a-z0-9]', '_'))
$mutex = New-Object System.Threading.Mutex($false, $mutexName)
$holderFile = Join-Path $stateDir 'lock-holder.txt'
$owned = $false
try {
    if ($Mode -eq 'post') {
        # post never waits for a pre-commit run (a batch Unity run holds the lock up to 45 minutes) and waits 2 minutes at
        # most for another post run (its own run usually fills the cache): within the hook's 600 s timeout.
        $owned = $mutex.WaitOne(0)
        if (-not $owned -and (Test-Path $holderFile) -and ((Get-Content $holderFile -Raw) -match '^precommit')) {
            Write-Output 'test-gate: a pre-commit run holds the gate; this check is skipped (the next call checks again).'
            exit 0
        }
        if (-not $owned) { $owned = $mutex.WaitOne([TimeSpan]::FromMinutes(2)) }
    } else {
        # Bounded wait: a hung holder (a stuck batch Unity) must not block every later commit forever.
        $owned = $mutex.WaitOne([TimeSpan]::FromMinutes(20))
    }
} catch [System.Threading.AbandonedMutexException] { $owned = $true }   # previous holder died: we own it now
if (-not $owned) {
    if ($Mode -eq 'post') {
        Write-Output 'test-gate: another gate run has held the lock for 2 minutes; this check is skipped (the next call checks again).'
        exit 0
    }
    [Console]::Error.WriteLine('test-gate: another gate run has held the lock for 20 minutes (a hung batch Unity?) - giving up.')
    exit 2
}
Set-Content -Path $holderFile -Value "$Mode $PID" -Encoding ascii

# ── 1. standalone battery ────────────────────────────────────────────────────

$cached = Get-Cached 'standalone' $saFp
$blocked = if ($Mode -eq 'precommit') { 'Commit blocked: ' } else { '' }

if ($null -eq $cached) {
    $runAll = Join-Path $root 'StandaloneTests/run-all.ps1'
    # Windows PowerShell 5.1 turns a native command's stderr into errors: do not stop on them.
    $ErrorActionPreference = 'Continue'
    $out = & $psExe -NoProfile -ExecutionPolicy Bypass -File $runAll 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $summary = @()
    if ($code -ne 0) {
        # the failing lines under their suite's "== name" header; a check's title ("- ...") is not a failure even when it
        # says "fails" or "errors" (it filled the summary with the titles of passing suites)
        $suite = ''
        foreach ($line in $out) {
            if ($line -match '^== ') { $suite = $line; continue }
            if ($line -match '^- ' -or $line -match ', 0 failed$|RemoteException') { continue }
            if ($line -match 'FAIL|EXCEPTION|[1-9]\d* failed|error|Error') {
                if ($suite) { $summary += $suite; $suite = '' }
                $summary += $line
            }
        }
        $summary = @($summary | Select-Object -First 60)
        if ($summary.Count -eq 0) { $summary = @($out | Select-Object -Last 40) }
    }
    Set-Cached 'standalone' $saFp ($code -eq 0) $summary
    if ($code -ne 0) { Set-Reported 'standalone' $saFp; Fail "${blocked}standalone tests FAILED (StandaloneTests/run-all.ps1):" $summary }
    if ($Mode -eq 'post') { Write-Output 'Standalone tests passed.' }
} elseif (-not $cached.ok -and -not ($Mode -eq 'post' -and (Test-Reported 'standalone' $saFp))) {
    $what = if ($Mode -eq 'precommit') { 'Commit blocked: the standalone tests fail' } else { 'Standalone tests still failing (sources unchanged since the failing run)' }
    Set-Reported 'standalone' $saFp
    Fail $what $cached.summary
}

# ── 2. Unity code compiles (no editor needed: StandaloneTests/unity-compile-check.ps1) ──

$cached = Get-Cached 'unitycompile' $ccFp
if (-not $onWindows) {
    # the check rewrites the Windows paths of Unity's generated csproj files; elsewhere the EditMode run compiles the code
    if ($Mode -eq 'precommit') { Write-Output 'Unity compile check skipped (Windows only); the EditMode run compiles the code.' }
} elseif ($null -eq $cached) {
    $ErrorActionPreference = 'Continue'
    $out = & $psExe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'StandaloneTests/unity-compile-check.ps1') 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $summary = if ($code -ne 0) { @($out | Where-Object { $_ -notmatch '^== ' } | Select-Object -First 40) } else { @() }
    Set-Cached 'unitycompile' $ccFp ($code -eq 0) $summary
    if ($code -ne 0) { Set-Reported 'unitycompile' $ccFp; Fail "${blocked}Unity code does NOT compile (StandaloneTests/unity-compile-check.ps1):" $summary }
} elseif (-not $cached.ok -and -not ($Mode -eq 'post' -and (Test-Reported 'unitycompile' $ccFp))) {
    $what = if ($Mode -eq 'precommit') { 'Commit blocked: the Unity code does not compile' } else { 'Unity code still does not compile (sources unchanged since the failing check)' }
    Set-Reported 'unitycompile' $ccFp
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
$lock = Join-Path $root 'Temp/UnityLockfile'
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
    $ver = (Select-String -Path (Join-Path $root 'ProjectSettings/ProjectVersion.txt') -Pattern '^m_EditorVersion:\s*(\S+)').Matches[0].Groups[1].Value
    $unity = if ($onWindows) { "C:\Program Files\Unity\Hub\Editor\$ver\Editor\Unity.exe" }
             elseif ($IsMacOS) { "/Applications/Unity/Hub/Editor/$ver/Unity.app/Contents/MacOS/Unity" }
             else { "$HOME/Unity/Hub/Editor/$ver/Editor/Unity" }
}
if (-not (Test-Path $unity)) {
    Fail "Commit blocked: Unity editor not found at '$unity' (set UNITY_EDITOR_PATH)." @()
}

# The external runner (ExternalRunner.Folder: Library/SimuLeanRunner/<engine fingerprint><runner sources suffix>) is
# built BEFORE Unity starts (2026-10-30). Built by the editor during the run, as it is on first use, its tests came out
# inconclusive (the build not finished) and the batch editor hung on exit until the 45-minute kill, after every engine
# change. Same algorithms as EngineFingerprint.OfSources / Tools/Runner/Fingerprint.targets and
# ExternalRunner.RunnerSourcesSuffix; a mismatch only costs a build (the editor then builds its own, as before).
function Get-Sha([System.Collections.Generic.IEnumerable[object]]$entries, [int]$bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    foreach ($e in $entries) {
        $text = [IO.File]::ReadAllText($e.Full, [Text.Encoding]::UTF8).Replace("`r", '')
        $b = [Text.Encoding]::UTF8.GetBytes($e.Name + "`n" + $text + "`n")
        [void]$sha.TransformBlock($b, 0, $b.Length, $null, 0)
    }
    [void]$sha.TransformFinalBlock([byte[]]@(), 0, 0)
    return (($sha.Hash[0..($bytes - 1)] | ForEach-Object { $_.ToString('x2') }) -join '')
}
function Get-RunnerFolder {
    $engine = (Resolve-Path (Join-Path $root 'Assets/SimuLean.Net')).Path.TrimEnd('\', '/')
    $files = @(Get-ChildItem -Path $engine -Recurse -File -Filter '*.cs' | Where-Object { $_.Extension -eq '.cs' } | ForEach-Object {
        [pscustomobject]@{ Name = $_.FullName.Substring($engine.Length + 1).Replace('\', '/'); Full = $_.FullName } })
    $files = [Linq.Enumerable]::ToArray([Linq.Enumerable]::OrderBy([object[]]$files, [Func[object, string]] { param($f) $f.Name }, [StringComparer]::Ordinal))
    $fp = Get-Sha $files 8
    $suffix = ''
    $runnerDir = Join-Path $root 'Tools/Runner'
    if (Test-Path $runnerDir) {
        $own = @(Get-ChildItem -Path $runnerDir -File | Where-Object { $_.Name -like '*.cs' -or $_.Name -like '*.csproj' -or $_.Name -like '*.targets' } |
            ForEach-Object { [pscustomobject]@{ Name = $_.Name; Full = $_.FullName } })
        $own = [Linq.Enumerable]::ToArray([Linq.Enumerable]::OrderBy([object[]]$own, [Func[object, string]] { param($f) $f.Name }, [StringComparer]::Ordinal))
        $suffix = '-' + (Get-Sha $own 4)
    }
    return Join-Path $root "Library/SimuLeanRunner/$fp$suffix"
}
$runnerProject = Join-Path $root 'Tools/Runner/simulean.csproj'
if ((Test-Path $runnerProject) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $runnerFolder = Get-RunnerFolder
    # same executable name and runtime id as ExternalRunner.ExeName / Rid
    $exeName = if ($onWindows) { 'simulean.exe' } else { 'simulean' }
    if (-not (Test-Path (Join-Path $runnerFolder $exeName))) {
        $arch = if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
        $rid = if ($onWindows) { "win-$arch" } elseif ($IsMacOS) { "osx-$arch" } else { "linux-$arch" }
        Write-Host "Building the external runner for this engine version ($(Split-Path $runnerFolder -Leaf))..."
        & dotnet publish "$runnerProject" -c Release -r $rid --self-contained true -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true `
            -o "$runnerFolder" --nologo -v quiet | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Host "WARNING: the runner build failed (exit $LASTEXITCODE); the editor builds it during the run." }
    }
}

$results = Join-Path $stateDir 'editmode-results.xml'
$log = Join-Path $stateDir 'editmode.log'
if (Test-Path $results) { Remove-Item $results -Force }
$hidden = if ($onWindows) { @{ WindowStyle = 'Hidden' } } else { @{} }   # -WindowStyle exists on Windows only
$p = Start-Process -FilePath $unity -PassThru @hidden -ArgumentList @(
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
