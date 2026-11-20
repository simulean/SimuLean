# Self-contained builds of the runner `simulean`: the .NET 10 runtime bundled, one file per OS, nothing
# to install. Output: Tools/Runner/dist/<rid>/simulean[.exe]. Needs the .NET 10 SDK.
#   powershell -File Tools/Runner/publish.ps1                 # win-x64, osx-arm64, linux-x64
#   powershell -File Tools/Runner/publish.ps1 -Rids win-x64
param([string[]]$Rids = @('win-x64', 'osx-arm64', 'linux-x64'))
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'simulean.csproj'
foreach ($rid in $Rids) {
    $out = Join-Path $PSScriptRoot "dist/$rid"
    # not trimmed nor AOT: the model codec and the engine build by reflection
    dotnet publish $project -c Release -r $rid --self-contained true -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true -o $out -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }
    Write-Host "simulean ($rid): $out"
}
