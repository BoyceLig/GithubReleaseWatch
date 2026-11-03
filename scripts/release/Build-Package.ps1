[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Tag,
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-x86', 'win-arm64')]
    [string] $RuntimeIdentifier
)

$ErrorActionPreference = 'Stop'
$releaseVersion = & (Join-Path $PSScriptRoot 'Get-ReleaseVersion.ps1') -Tag $Tag
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$releaseDirectory = Join-Path $repositoryRoot 'artifacts/releases'
$publishDirectory = Join-Path $releaseDirectory ("staging/$RuntimeIdentifier-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
$project = Join-Path $repositoryRoot 'GithubReleaseWatch/GithubReleaseWatch.csproj'

# Framework-dependent directory publish: preserve DLLs, never bundle into one EXE.
dotnet publish $project -c Release -r $RuntimeIdentifier --self-contained false -o $publishDirectory `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Publish failed for $RuntimeIdentifier" }

foreach ($name in @('GithubReleaseWatch.exe', 'GithubReleaseWatch.dll', 'config.json', 'LICENSE', 'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll', 'Microsoft.Windows.SDK.NET.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $name))) { throw "Missing package file: $name" }
}
$loaders = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File -Filter 'WebView2Loader.dll')
if ($loaders.Count -lt 1) { throw "Missing $RuntimeIdentifier WebView2 loader" }

# Verify native EXE/loader architecture even when cross-publishing from an x64 runner.
$expectedMachine = @{ 'win-x64' = 0x8664; 'win-x86' = 0x014c; 'win-arm64' = 0xaa64 }[$RuntimeIdentifier]
$nativeFiles = @((Join-Path $publishDirectory 'GithubReleaseWatch.exe')) + @($loaders.FullName)
foreach ($nativeFile in $nativeFiles) {
    $stream = [IO.File]::OpenRead($nativeFile)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        $stream.Position = 0x3c
        $peOffset = $reader.ReadInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne $expectedMachine) {
            throw "Incorrect $RuntimeIdentifier native architecture: $nativeFile"
        }
    }
    finally { $reader.Dispose() }
}

$config = Get-Content -LiteralPath (Join-Path $publishDirectory 'config.json') -Raw | ConvertFrom-Json
if (-not [string]::IsNullOrWhiteSpace($config.Token) -or
    -not [string]::IsNullOrWhiteSpace($config.Proxy.Username) -or
    -not [string]::IsNullOrWhiteSpace($config.Proxy.Password)) {
    throw 'Default configuration contains credentials. Do not include personal settings in a release.'
}

# XML reference documentation and debug symbols are not needed to run the application.
Get-ChildItem -LiteralPath $publishDirectory -File |
    Where-Object { $_.Extension -in '.xml', '.pdb' } | Remove-Item -Force

$archiveName = "GithubReleaseWatch-$Tag-$RuntimeIdentifier.zip"
$archivePath = Join-Path $releaseDirectory $archiveName
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archivePath -Force
$checksum = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archivePath.sha256" -Value "$checksum  $archiveName" -Encoding ascii
Write-Host "Created $archivePath (version $releaseVersion)"
