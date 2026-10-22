# Compile-checks the Unity C# code WITHOUT the Unity editor (it may be open by someone else, or closed).
#
# Uses the project files Unity generates for IDEs (Assembly-CSharp.csproj / Assembly-CSharp-Editor.csproj:
# defines, analyzers, references to the Unity / package DLLs) as templates, but decides the sources itself,
# with Unity's rules, so files and asmdefs added since Unity last wrote the project files are included:
#   - every *.asmdef folder is its own assembly (its references, precompiledReferences, noEngineReferences);
#   - the remaining *.cs under Assets go to Assembly-CSharp, or Assembly-CSharp-Editor inside "Editor" folders;
#     both reference every autoReferenced asmdef assembly.
# Output goes to %TEMP%; nothing in the project (Temp/, Library/) is touched.
#
#   powershell -ExecutionPolicy Bypass -File StandaloneTests/unity-compile-check.ps1
#
# Exit 0 = everything compiles; 1 = compiler errors (listed once each). Needs the .NET SDK and the generated
# csproj files (open the project once in Unity with an IDE package, e.g. Visual Studio / VS Code).
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path $env:TEMP ('SimuLeanCompileCheck_' + (($root.ToLowerInvariant() -replace '[^a-z0-9]', '_')))
New-Item -ItemType Directory -Force -Path $work | Out-Null

$runtimeProj = Join-Path $root 'Assembly-CSharp.csproj'
$editorProj = Join-Path $root 'Assembly-CSharp-Editor.csproj'
if (-not (Test-Path $runtimeProj) -or -not (Test-Path $editorProj)) {
    Write-Host 'Generated Assembly-CSharp*.csproj not found: open the project in Unity once (IDE package installed).' -ForegroundColor Red
    exit 1
}
$assets = Join-Path $root 'Assets'

function Convert-Project([string]$source, [string]$name, [string[]]$include, [string[]]$exclude, [string[]]$projectRefs,
                         [bool]$engineFree = $false, [string[]]$extraDlls = @()) {
    $text = Get-Content $source -Raw
    # Unity's explicit file lists, non-code items and project references are replaced below.
    $text = [regex]::Replace($text, '(?m)^\s*<(Compile|None) Include="[^"]*"\s*/>\s*\r?\n', '')
    $text = [regex]::Replace($text, '(?m)^\s*<ProjectReference Include="[^"]*"\s*/>\s*\r?\n', '')
    $text = [regex]::Replace($text, '(?s)\s*<ProjectReference Include="[^"]*">.*?</ProjectReference>', '')
    # Relative paths are relative to the project root.
    $text = [regex]::Replace($text, '<HintPath>(?![A-Za-z]:\\)([^<]+)</HintPath>', { param($m) "<HintPath>$root\$($m.Groups[1].Value)</HintPath>" })
    $text = [regex]::Replace($text, '<Analyzer Include="(?![A-Za-z]:\\)([^"]+)"', { param($m) "<Analyzer Include=`"$root\$($m.Groups[1].Value)`"" })
    if ($engineFree) {
        # noEngineReferences / overrideReferences: only the .NET reference assemblies stay (then the listed DLLs).
        $text = [regex]::Replace($text, '(?s)\s*<Reference Include="[^"]*">\s*<HintPath>(?<p>[^<]*)</HintPath>.*?</Reference>', {
            param($m) if ($m.Groups['p'].Value -match 'UnityReferenceAssemblies') { $m.Value } else { '' } })
    }
    $text = [regex]::Replace($text, '<AssemblyName>[^<]*</AssemblyName>', "<AssemblyName>$name</AssemblyName>")
    $obj = Join-Path $work "obj\$name"
    $bin = Join-Path $work "bin\$name"
    $text = [regex]::Replace($text, '<BaseIntermediateOutputPath>[^<]*</BaseIntermediateOutputPath>', "<BaseIntermediateOutputPath>$obj\</BaseIntermediateOutputPath>")
    $text = [regex]::Replace($text, '<OutputPath>[^<]*</OutputPath>', "<OutputPath>$bin\</OutputPath>")
    $items = @($include | ForEach-Object { "    <Compile Include=`"$_`" Exclude=`"$($exclude -join ';')`" />" })
    $items += @($extraDlls | Where-Object { $_ } | ForEach-Object { "    <Reference Include=`"$([IO.Path]::GetFileNameWithoutExtension($_))`"><HintPath>$_</HintPath><Private>False</Private></Reference>" })
    $items += @($projectRefs | Where-Object { $_ } | ForEach-Object { "    <ProjectReference Include=`"$_`" />" })
    $text = $text.Replace('</Project>', "  <ItemGroup>`r`n$($items -join "`r`n")`r`n  </ItemGroup>`r`n</Project>")
    $out = Join-Path $work "$name.csproj"
    Set-Content -Path $out -Value $text -Encoding utf8
    return $out
}

# ── asmdef assemblies ────────────────────────────────────────────────────────
$asmdefs = @{}
foreach ($f in Get-ChildItem -Path $assets -Recurse -Filter *.asmdef -File) {
    $j = (Get-Content $f.FullName -Raw) | ConvertFrom-Json
    $asmdefs[$j.name] = @{ Dir = $f.DirectoryName; Def = $j; Proj = (Join-Path $work "$($j.name).csproj") }
}
$allDlls = @(Get-ChildItem -Path $assets -Recurse -Filter *.dll -File | ForEach-Object { $_.FullName })
foreach ($name in $asmdefs.Keys) {
    $a = $asmdefs[$name]; $d = $a.Def
    $refs = @(); foreach ($r in @($d.references)) { if ($r -and $asmdefs.ContainsKey($r)) { $refs += $asmdefs[$r].Proj } }
    $dlls = if ($d.overrideReferences) { @($allDlls | Where-Object { @($d.precompiledReferences) -contains [IO.Path]::GetFileName($_) }) } else { $allDlls }
    $nested = @($asmdefs.Values | Where-Object { $_.Dir -ne $a.Dir -and $_.Dir.StartsWith($a.Dir + '\') } | ForEach-Object { "$($_.Dir)\**\*.cs" })
    [void](Convert-Project $runtimeProj $name @("$($a.Dir)\**\*.cs") $nested $refs ([bool]$d.noEngineReferences -or [bool]$d.overrideReferences) $dlls)
}

# ── Assembly-CSharp / Assembly-CSharp-Editor ─────────────────────────────────
$asmdefExcludes = @($asmdefs.Values | ForEach-Object { "$($_.Dir)\**\*.cs" })
$autoRefs = @($asmdefs.Values | Where-Object { $_.Def.autoReferenced -ne $false } | ForEach-Object { $_.Proj })
$editorDirs = "$assets\**\Editor\**\*.cs"
$rt = Convert-Project $runtimeProj 'Assembly-CSharp' @("$assets\**\*.cs") (@($editorDirs) + $asmdefExcludes) $autoRefs
$ed = Convert-Project $editorProj 'Assembly-CSharp-Editor' @($editorDirs) $asmdefExcludes (@($rt) + $autoRefs)

# Building the editor project builds everything it references; one build, errors listed once.
Write-Host "== $(@($asmdefs.Keys) -join ', ')$(if ($asmdefs.Count) { ', ' })Assembly-CSharp, Assembly-CSharp-Editor" -ForegroundColor Cyan
$out = & dotnet build $ed -nologo -v quiet -clp:NoSummary 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) {
    $out | Where-Object { $_ -match ': error ' } | ForEach-Object { ($_ -replace '\s*\[[^\]]+\.csproj\]$', '') } |
        Sort-Object -Unique | ForEach-Object { Write-Host $_ }
    Write-Host 'Unity code does NOT compile.' -ForegroundColor Red
    exit 1
}
Write-Host "Unity code compiles ($($asmdefs.Count) asmdef assemblies + runtime + editor)." -ForegroundColor Green
exit 0
