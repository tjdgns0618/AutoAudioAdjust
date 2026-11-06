$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$distRoot = Join-Path $root "dist"
$standardOutput = Join-Path $distRoot "AutoAudioAdjust"
$portableOutput = Join-Path $distRoot "AutoAudioAdjust-Portable"

function Reset-ReleaseDirectory([string] $path) {
    $fullDistRoot = [System.IO.Path]::GetFullPath($distRoot).TrimEnd('\') + '\'
    $fullTarget = [System.IO.Path]::GetFullPath($path).TrimEnd('\') + '\'

    if (-not $fullTarget.StartsWith($fullDistRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the dist directory: $fullTarget"
    }

    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $path "App") -Force | Out-Null
}

function Publish-Launcher([string] $output) {
    dotnet publish (Join-Path $root "AutoAudioAdjust.Launcher\AutoAudioAdjust.Launcher.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:PublishAot=true -p:StripSymbols=true -p:DebugType=None -p:InvariantGlobalization=true `
        -o $output

    $nativeSymbols = Join-Path $output "AutoAudioAdjust Launcher.pdb"
    if (Test-Path -LiteralPath $nativeSymbols) {
        Remove-Item -LiteralPath $nativeSymbols -Force
    }
}

New-Item -ItemType Directory -Path $distRoot -Force | Out-Null
Reset-ReleaseDirectory $standardOutput
Reset-ReleaseDirectory $portableOutput

dotnet test (Join-Path $root "AutoAudioAdjust.sln") -c Release

# Small framework-dependent edition. Requires .NET 9 Desktop Runtime.
dotnet publish (Join-Path $root "AutoAudioAdjust.App\AutoAudioAdjust.App.csproj") `
    -c Release -r win-x64 --self-contained false -p:DebugType=None `
    -o (Join-Path $standardOutput "App")
Publish-Launcher $standardOutput
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $standardOutput "README.md")

# Portable edition. The main application and launcher both include their runtime.
dotnet publish (Join-Path $root "AutoAudioAdjust.App\AutoAudioAdjust.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None `
    -o (Join-Path $portableOutput "App")
Publish-Launcher $portableOutput
Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination (Join-Path $portableOutput "README.md")

Compress-Archive -Path (Join-Path $standardOutput "*") `
    -DestinationPath (Join-Path $distRoot "AutoAudioAdjust-win-x64.zip") `
    -CompressionLevel Optimal -Force
Compress-Archive -Path (Join-Path $portableOutput "*") `
    -DestinationPath (Join-Path $distRoot "AutoAudioAdjust-Portable-win-x64.zip") `
    -CompressionLevel Optimal -Force

Write-Host "Both release packages were built from the latest source:" -ForegroundColor Green
Get-Item `
    (Join-Path $distRoot "AutoAudioAdjust-win-x64.zip"), `
    (Join-Path $distRoot "AutoAudioAdjust-Portable-win-x64.zip") |
    Select-Object FullName, Length, LastWriteTime
