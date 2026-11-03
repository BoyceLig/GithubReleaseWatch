[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Tag
)

$ErrorActionPreference = 'Stop'
if ($Tag -cnotmatch '^v?(?<Version>(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?)$') {
    throw "Invalid release tag '$Tag'. Use 1.0.1, v1.0.1 or v1.0.2-beta.1."
}
$tagVersion = $Matches.Version
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
[xml] $project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'GithubReleaseWatch/GithubReleaseWatch.csproj') -Raw
$projectVersion = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($tagVersion -cne $projectVersion) {
    throw "Tag version '$tagVersion' does not match project Version '$projectVersion'. Update the project before tagging."
}
$tagVersion
