param([switch]$FrameworkDependent, [string]$OutputDirectory = '')

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$appDirectory = Join-Path $projectRoot 'windows/ShareLink.App'
$projectFile = Join-Path $appDirectory 'ShareLink.App.csproj'
$cargo = (Get-Command cargo -ErrorAction Stop).Source
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

if (!$OutputDirectory) {
    [xml]$project = Get-Content -LiteralPath $projectFile -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    $OutputDirectory = "artifacts/windows-$version"
}
$output = [IO.Path]::GetFullPath($OutputDirectory, $projectRoot)

Push-Location -LiteralPath $projectRoot
try {
    & $cargo build --release --locked --target-dir (Join-Path $projectRoot 'target')
    if ($LASTEXITCODE -ne 0) { throw 'Rust core build failed.' }
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    & $dotnet publish $projectFile -c Release -r win-x64 --self-contained $selfContained -o $output
    if ($LASTEXITCODE -ne 0) { throw 'Windows publish failed.' }
    if (!(Test-Path -LiteralPath (Join-Path $output 'sharelink_core.dll'))) {
        throw 'Publish output is missing the Rust core. Use the x64 Windows MSVC Rust toolchain.'
    }
    Write-Output (Join-Path $output 'ShareLink.exe')
} finally { Pop-Location }
