# Self-contained builds of the runner `simulean`: the .NET 10 runtime bundled, one file per OS, nothing
# to install. Output: Tools/Runner/dist/<rid>/simulean[.exe]. Needs the .NET 10 SDK.
#   powershell -File Tools/Runner/publish.ps1                 # win-x64, osx-arm64, linux-x64
#   powershell -File Tools/Runner/publish.ps1 -Rids win-x64
#   powershell -File Tools/Runner/publish.ps1 -Version 1.3.0  # also dist/SimuLeanRunner-<version>-<rid>.zip, the
#                                                             # release's assets (RunnerRelease, ExternalRunner's download)
param([string[]]$Rids = @('win-x64', 'osx-arm64', 'linux-x64'), [string]$Version = '')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$project = Join-Path $PSScriptRoot 'simulean.csproj'
foreach ($rid in $Rids) {
    $out = Join-Path $PSScriptRoot "dist/$rid"
    # not trimmed nor AOT: the model codec and the engine build by reflection
    dotnet publish $project -c Release -r $rid --self-contained true -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=true -o $out -v quiet --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish $rid failed" }
    Write-Host "simulean ($rid): $out"
    if ($Version) {
        # one entry, SimuLeanRunner/<rid>/simulean[.exe], with '/' separators (the editor extracts it by that name)
        $exe = if ($rid -like 'win-*') { 'simulean.exe' } else { 'simulean' }
        $zip = Join-Path $PSScriptRoot "dist/SimuLeanRunner-$Version-$rid.zip"
        if (Test-Path $zip) { Remove-Item $zip }
        $archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
        try {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $out $exe),
                "SimuLeanRunner/$rid/$exe", [System.IO.Compression.CompressionLevel]::Optimal)
        } finally { $archive.Dispose() }
        Write-Host "  release asset: $zip"
    }
}
