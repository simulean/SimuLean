# PowerShell Script to Restore NuGet Packages for Unity Project
# This script downloads and extracts NuGet packages defined in Assets\packages.config

Write-Host "=== NuGet Package Restoration Script ===" -ForegroundColor Green
Write-Host ""

# Configuration
$packagesConfigPath = "Assets\packages.config"
$packagesFolder = "Assets\Packages"
$nugetSource = "https://api.nuget.org/v3/index.json"

# Check if nuget.exe exists, if not download it
$nugetExe = ".\nuget.exe"
if (-not (Test-Path $nugetExe)) {
    Write-Host "Downloading nuget.exe..." -ForegroundColor Yellow
    $nugetUrl = "https://dist.nuget.org/win-x86-commandline/latest/nuget.exe"
    Invoke-WebRequest -Uri $nugetUrl -OutFile $nugetExe
    Write-Host "nuget.exe downloaded successfully!" -ForegroundColor Green
}

# Read packages.config
if (-not (Test-Path $packagesConfigPath)) {
    Write-Host "Error: packages.config not found at $packagesConfigPath" -ForegroundColor Red
    exit 1
}

Write-Host "Reading $packagesConfigPath..." -ForegroundColor Cyan
[xml]$packagesConfig = Get-Content $packagesConfigPath

# Create packages folder if it doesn't exist
if (-not (Test-Path $packagesFolder)) {
    New-Item -ItemType Directory -Path $packagesFolder | Out-Null
}

# Install each package
foreach ($package in $packagesConfig.packages.package) {
    $id = $package.id
    $version = $package.version
    
    Write-Host ""
    Write-Host "Installing $id version $version..." -ForegroundColor Yellow
    
    # Install package to a temp directory
    $tempDir = ".\temp_packages"
    & $nugetExe install $id -Version $version -OutputDirectory $tempDir -Source $nugetSource -NonInteractive
    
    # Copy the package to Assets\Packages
    $packageDir = "$tempDir\$id.$version"
    $targetDir = "$packagesFolder\$id.$version"
    
    if (Test-Path $packageDir) {
        # Create target directory if it doesn't exist
        if (-not (Test-Path $targetDir)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }
        
        # Copy lib folder (contains DLLs)
        $libFolder = "$packageDir\lib"
        if (Test-Path $libFolder) {
            Copy-Item -Path $libFolder -Destination $targetDir -Recurse -Force
            Write-Host "  ? Copied DLLs for $id" -ForegroundColor Green
        }
        
        # Copy any other important files
        $filesToCopy = @("*.nuspec", "LICENSE*", "THIRD-PARTY-NOTICES*")
        foreach ($pattern in $filesToCopy) {
            Get-ChildItem -Path $packageDir -Filter $pattern | ForEach-Object {
                Copy-Item -Path $_.FullName -Destination $targetDir -Force
            }
        }
        
        Write-Host "  ? Package $id restored successfully" -ForegroundColor Green
    } else {
        Write-Host "  ? Failed to download $id" -ForegroundColor Red
    }
}

# Clean up temp directory
if (Test-Path ".\temp_packages") {
    Remove-Item -Path ".\temp_packages" -Recurse -Force
}

Write-Host ""
Write-Host "=== Restoration Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "1. Close and reopen Unity Editor" -ForegroundColor White
Write-Host "2. Unity will detect the new DLLs and reimport them" -ForegroundColor White
Write-Host "3. Check Assets ? Packages folder for the restored packages" -ForegroundColor White
Write-Host "4. Verify references in your C# project are working" -ForegroundColor White
