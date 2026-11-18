param(
    [string]$PublishDirectory = '',
    [string]$OutputDirectory = '',
    [string]$CompilerPath = '',
    [switch]$Rebuild,
    [switch]$TestBuild,
    [string]$TestVersion = ''
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
function ProjectPath([string]$Value) {
    if ([IO.Path]::IsPathRooted($Value)) { return [IO.Path]::GetFullPath($Value) }
    return [IO.Path]::GetFullPath((Join-Path $projectRoot $Value))
}
if (!$PublishDirectory) {
    [xml]$project = Get-Content -LiteralPath (Join-Path $projectRoot 'windows/ShareLink.App/ShareLink.App.csproj') -Raw
    $publicVersion = [string]$project.Project.PropertyGroup.Version
    if ($publicVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid project version.' }
    $PublishDirectory = "artifacts/windows-$publicVersion"
}
$publish = ProjectPath $PublishDirectory
if ($Rebuild) {
    & (Join-Path $PSScriptRoot 'build-windows.ps1') -OutputDirectory $publish
    if (!$?) { throw 'Windows build failed.' }
}
foreach ($required in @('ShareLink.exe','ShareLink.dll','sharelink_core.dll','coreclr.dll','hostfxr.dll','ShareLink.runtimeconfig.json','Microsoft.AspNetCore.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $publish $required) -PathType Leaf)) { throw "Complete self-contained publish required; missing $required" }
}
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $publish 'ShareLink.exe'))
$version = ($info.ProductVersion -split '\+')[0]
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Executable product version must use semantic versioning.' }
$numeric = $info.FileVersion
if ($TestVersion) {
    if (!$TestBuild) { throw 'TestVersion is restricted to isolated test installers.' }
    if ($TestVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid test version.' }
    $version = $TestVersion
    $numeric = "$version.0"
}
if (!$OutputDirectory) { $OutputDirectory = "artifacts/installers/$version" }
$output = ProjectPath $OutputDirectory
if (!$CompilerPath) {
    $candidates = @((Join-Path $projectRoot '.tools/inno-setup/ISCC.exe'), (Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Inno Setup 6/ISCC.exe'))
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$CompilerPath) { $CompilerPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
}
if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath)) { throw 'Install Inno Setup 6.7+ or pass -CompilerPath.' }
$stage = Join-Path $projectRoot ('artifacts/installer-stage/' + $version + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage,$output -Force | Out-Null
# A clean allowlisted payload keeps private diagnostics, identities, keys and PDBs out of releases.
$manifest = foreach ($file in Get-ChildItem -LiteralPath $publish -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($publish, $file.FullName)
    if ($file.Extension -in @('.pdb','.md')) { continue }
    if ($file.Extension -notin @('.dll','.exe','.json') -and $relative -ne 'Lucide.LICENSE.txt') { throw "Unexpected publish file: $relative" }
    if ($file.Extension -eq '.json' -and $relative -notin @('ShareLink.runtimeconfig.json','ShareLink.deps.json','transport-modules.json')) { throw "Unexpected JSON in publish: $relative" }
    if ($file.Extension -eq '.exe' -and $relative -notin @('ShareLink.exe','createdump.exe')) { throw "Unexpected executable in publish: $relative" }
    if ($relative -match '(^|[\/])(identity|received|validation|evidence|state|logs?)([\/]|$)') { throw 'Private data found in publish.' }
    $destination = Join-Path $stage $relative
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    [pscustomobject]@{ Path=$relative.Replace('\','/'); Bytes=$file.Length; SHA256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$arguments = @('/Qp',"/DPayloadDirectory=$stage","/DAppVersion=$version","/DNumericVersion=$numeric","/DOutputDirectory=$output")
if ($TestBuild) { $arguments += '/DTestBuild' }
$arguments += (Join-Path $projectRoot 'installer/ShareLink.iss')
& $CompilerPath @arguments
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
$setup = Join-Path $output 'ShareLink-Setup-x64.exe'
if (!(Test-Path -LiteralPath $setup)) { throw 'Compiler did not produce setup.' }
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  ShareLink-Setup-x64.exe" + [Environment]::NewLine)
@{ Version=$version; NumericVersion=$numeric; TestBuild=[bool]$TestBuild; Files=@($manifest); SetupSHA256=$hash } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'payload-manifest.json') -Encoding utf8
Write-Output "Ready: $setup"
