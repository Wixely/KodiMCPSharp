[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot (Join-Path 'artifacts' $Runtime)
}

dotnet publish (Join-Path $repositoryRoot 'src\KodiMCPSharp\KodiMCPSharp.csproj') `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -o $OutputPath `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=portable `
    -p:DebugSymbols=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Published KodiMCPSharp to $OutputPath"
