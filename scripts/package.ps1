# A release contains a portable archive, an MSI and a setup EXE that embeds the same MSI.
# Both application variants are self-contained and use their own AA Desk startup entry.
#
# They come from two publishes, because the portable one is a single executable and the installer needs
# the ordinary folder. Measured, interleaved, on this machine: the bundle costs nothing to load (1020 ms
# against the folder's 1024 ms), and it is the same 140 MB either way, so the archive stays about 61 MB.
# EnableCompressionInSingleFile was tried and dropped: 67 MB as a bare file, but 2028 ms to load, every
# launch and not just the first — a bad trade for something that starts at sign-in.
#
# The installer uses a per-machine MSI under Program Files and requests elevation.
# The portable archive runs without installation.
[CmdletBinding()]
param([switch]$SkipTests, [switch]$SkipInstaller)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools/dotnet'
if (Test-Path -LiteralPath (Join-Path $localDotnet 'dotnet.exe')) { $env:PATH = $localDotnet + ';' + $env:PATH }
$solutionPath = Join-Path $projectRoot 'DownloadsStack.slnx'
$projectPath = Join-Path $projectRoot 'src/DownloadsStack/DownloadsStack.csproj'
$artifactsPath = Join-Path $projectRoot 'artifacts'
$stagePath = Join-Path $artifactsPath 'stage'
$safeStage = [IO.Path]::GetFullPath($stagePath)
$safeArtifacts = [IO.Path]::GetFullPath($artifactsPath) + [IO.Path]::DirectorySeparatorChar
if (-not $safeStage.StartsWith($safeArtifacts, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging path.' }
$appPath = Join-Path $stagePath 'app'
$singlePath = Join-Path $stagePath 'single'

$version = @(([xml](Get-Content -LiteralPath $projectPath)).Project.PropertyGroup |
    ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw 'The project carries no <Version>.' }
Write-Output "AA Desk $version"

if (-not $SkipTests) {
    dotnet test $solutionPath -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

# A stage of its own, emptied first: the archive and the installer must carry the application and
# nothing else. artifacts/win-x64 from publish.ps1 also holds the documents and the check's own log.
if (Test-Path -LiteralPath $stagePath) { Remove-Item -LiteralPath $stagePath -Recurse -Force }
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o $appPath
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination (Join-Path $appPath 'LICENSE')

$executable = Join-Path $appPath 'AA Desk.exe'
$uiCheck = Start-Process -FilePath $executable -ArgumentList '--check-ui' -WindowStyle Hidden -Wait -PassThru
$uiCheckLog = Join-Path $appPath 'ui-check.log'
if ($uiCheck.ExitCode -ne 0) {
    Get-Content -LiteralPath $uiCheckLog
    throw 'Published application resources failed to load.'
}
Remove-Item -LiteralPath $uiCheckLog -Force # Written by the check above, not part of the application.

$outputs = @()

if (-not $SkipInstaller) {
    # WiX is a local tool of this repository: restoring it is most of the installer toolchain. The wizard
    # dialogs live in an extension, which the tool keeps in its own cache rather than in the repository,
    # so adding it here is what makes a fresh clone build the same package. Adding it twice is not an error.
    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Restoring the WiX tool failed.' }
    dotnet wix extension add -g WixToolset.UI.wixext/6.0.2
    if ($LASTEXITCODE -ne 0) { throw 'Adding the WiX UI extension failed.' }
    $msiPath = Join-Path $artifactsPath "AADesk-$version-win-x64.msi"
    $licensePath = Join-Path $projectRoot 'packaging/License.rtf'
    Write-Output 'Building the installer (compressing ~140 MB into a cabinet takes a few minutes)...'
    dotnet wix build (Join-Path $projectRoot 'packaging/DownloadsStack.wxs') `
        -arch x64 -ext WixToolset.UI.wixext -d "Version=$version" -d "PublishDir=$appPath" `
        -d "LicenseRtf=$licensePath" -o $msiPath
    if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }
    $outputs += $msiPath

    # Burn wraps the same MSI in a single setup.exe with an install/repair/uninstall UI.
    dotnet wix extension add -g WixToolset.BootstrapperApplications.wixext/6.0.2
    if ($LASTEXITCODE -ne 0) { throw 'Adding the WiX bootstrapper extension failed.' }
    $setupPath = Join-Path $artifactsPath "AADesk-$version-setup-win-x64.exe"
    dotnet wix build (Join-Path $projectRoot 'packaging/AADesk.Bundle.wxs') `
        -arch x64 -ext WixToolset.BootstrapperApplications.wixext -d "Version=$version" `
        -d "MsiPath=$msiPath" -d "LicenseRtf=$licensePath" `
        -d "IconFile=$(Join-Path $projectRoot 'src/DownloadsStack/Assets/app.ico')" `
        -d "LogoFile=$(Join-Path $projectRoot 'src/DownloadsStack/Assets/app.png')" -o $setupPath
    if ($LASTEXITCODE -ne 0) { throw 'Building the setup executable failed.' }
    $outputs += $setupPath
    $setupHash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
    $hashPath = $setupPath + '.sha256'
    [IO.File]::WriteAllText($hashPath, "$setupHash  $(Split-Path -Leaf $setupPath)`n", [Text.UTF8Encoding]::new($false))
    $outputs += $hashPath
}

# The portable build keeps the executable self-contained; the archive also carries the upstream MIT notice.
dotnet publish $projectPath -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false -o $singlePath
if ($LASTEXITCODE -ne 0) { throw 'Publishing the single file failed.' }

$singleExecutable = Join-Path $singlePath 'AA Desk.exe'
$singleCheck = Start-Process -FilePath $singleExecutable -ArgumentList '--check-ui' -WindowStyle Hidden -Wait -PassThru
if ($singleCheck.ExitCode -ne 0) {
    Get-Content -LiteralPath (Join-Path $singlePath 'ui-check.log')
    throw 'The single-file application failed to load its resources.'
}

$zipPath = Join-Path $artifactsPath "AADesk-$version-portable-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression # ZipArchiveMode lives here, the rest next door.
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Not Compress-Archive: it takes minutes over a file this size and this takes seconds.
$archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $archive, $singleExecutable, 'AA Desk.exe',
        [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
        $archive, (Join-Path $projectRoot 'LICENSE'), 'LICENSE',
        [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
} finally { $archive.Dispose() }
$outputs += $zipPath

Remove-Item -LiteralPath $stagePath -Recurse -Force
foreach ($output in $outputs) {
    $file = Get-Item -LiteralPath $output
    '{0}  {1:N1} MB  {2}' -f $file.Name, ($file.Length / 1MB), (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash
}
