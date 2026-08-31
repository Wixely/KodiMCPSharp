[CmdletBinding()]
param(
    [string]$Executable = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\win-x64\KodiMCPSharp.exe'),
    [ValidateRange(1024, 65535)]
    [int]$Port = 58080,
    [ValidateRange(1, 1000)]
    [int]$ExpectedToolCount = 34,
    [switch]$ProbeAddons,
    [switch]$AllowLocalConfiguration,
    [ValidateRange(0, 3)]
    [int]$AddonTraversalDepth = 0,
    [ValidateLength(0, 200)]
    [string]$PreferredAddonName = '',
    [string[]]$AddonMenuPath = @()
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path $Executable).Path
$serverProcess = Start-Process -FilePath $resolvedExecutable `
    -ArgumentList "--Server:Port=$Port" `
    -WorkingDirectory (Split-Path $resolvedExecutable) `
    -WindowStyle Hidden `
    -PassThru

try {
    $health = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        try {
            $health = Invoke-RestMethod -Uri "http://localhost:$Port/healthz" -TimeoutSec 2
            break
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }
    if ($null -eq $health) { throw 'Packaged server did not become healthy.' }

    $headers = @{
        Accept = 'application/json, text/event-stream'
        'MCP-Protocol-Version' = '2025-06-18'
    }
    $initialize = @{
        jsonrpc = '2.0'
        id = 1
        method = 'initialize'
        params = @{
            protocolVersion = '2025-06-18'
            capabilities = @{}
            clientInfo = @{ name = 'package-smoke'; version = '1.0' }
        }
    } | ConvertTo-Json -Depth 5 -Compress
    $initializeResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
        -Method Post -ContentType 'application/json' -Headers $headers -Body $initialize -TimeoutSec 10
    $initializeDataLine = $initializeResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
    $initializeJson = if ($initializeDataLine) { $initializeDataLine.Substring(5).Trim() } else { $initializeResponse.Content }
    $initializePayload = $initializeJson | ConvertFrom-Json

    $listTools = @{ jsonrpc = '2.0'; id = 2; method = 'tools/list'; params = @{} } | ConvertTo-Json -Compress
    $toolsResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
        -Method Post -ContentType 'application/json' -Headers $headers -Body $listTools -TimeoutSec 10
    $dataLine = $toolsResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
    $jsonText = if ($dataLine) { $dataLine.Substring(5).Trim() } else { $toolsResponse.Content }
    $payload = $jsonText | ConvertFrom-Json
    $tools = @($payload.result.tools)
    $forbiddenInputs = @('method', 'json', 'path', 'plugin', 'directory', 'url', 'endpoint', 'credential', 'password')
    $runtimeInputs = @($tools | ForEach-Object { @($_.inputSchema.properties.PSObject.Properties.Name) })
    $foundForbiddenInputs = @($runtimeInputs | Where-Object { $forbiddenInputs -contains $_ })
    $publishDirectory = Split-Path $resolvedExecutable

    if ($health.status -ne 'ok') { throw "Unexpected health status '$($health.status)'." }
    if ($initializePayload.result.protocolVersion -ne '2025-06-18') { throw 'Packaged server negotiated an unexpected MCP protocol.' }
    if ($tools.Count -ne $ExpectedToolCount) { throw "Expected $ExpectedToolCount tools but discovered $($tools.Count)." }
    if ($foundForbiddenInputs.Count -gt 0) { throw 'Packaged tool schemas expose a forbidden raw-target input.' }
    if (-not ($tools | Where-Object name -eq 'kodi_resume')) { throw 'Packaged server is missing kodi_resume.' }
    if (-not ($tools | Where-Object name -eq 'kodi_play_movie')) { throw 'Packaged server is missing kodi_play_movie.' }
    if (-not ($tools | Where-Object name -eq 'kodi_play_episode')) { throw 'Packaged server is missing kodi_play_episode.' }
    $localConfigurationIncluded = Test-Path (Join-Path $publishDirectory 'KodiMCPSharp.Local.json')
    if ($localConfigurationIncluded -and -not $AllowLocalConfiguration) { throw 'Private local configuration was included in the package.' }

    $summary = [ordered]@{
        Health = $health.status
        ReadOnly = $health.readOnly
        ConfiguredInstances = $health.configuredInstances
        InitializeStatus = $initializeResponse.StatusCode
        Protocol = $initializePayload.result.protocolVersion
        ToolCount = $tools.Count
        ForbiddenInputCount = $foundForbiddenInputs.Count
        LocalConfigurationIncluded = $localConfigurationIncluded
    }
    if ($ProbeAddons) {
        if ($health.configuredInstances -lt 1) { throw 'Add-on probing requires a configured instance.' }
        $callAddons = @{
            jsonrpc = '2.0'
            id = 3
            method = 'tools/call'
            params = @{ name = 'kodi_list_addons'; arguments = @{ page = 0; pageSize = 50 } }
        } | ConvertTo-Json -Depth 5 -Compress
        $addonResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
            -Method Post -ContentType 'application/json' -Headers $headers -Body $callAddons -TimeoutSec 20
        $addonDataLine = $addonResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -First 1
        $addonJson = if ($addonDataLine) { $addonDataLine.Substring(5).Trim() } else { $addonResponse.Content }
        $addonPayload = $addonJson | ConvertFrom-Json
        if ($addonPayload.error) { throw "The packaged add-on listing call failed: $($addonPayload.error.message)" }
        if ($addonPayload.result.isError) {
            throw "The packaged add-on listing call failed: $($addonPayload.result.content[0].text)"
        }
        $addonPage = $addonPayload.result.content[0].text | ConvertFrom-Json
        $summary.AddonCount = @($addonPage.addons).Count
        $summary.BrowsableAddonCount = @($addonPage.addons | Where-Object browsable).Count

        if ($AddonTraversalDepth -gt 0) {
            $browsableAddons = @($addonPage.addons | Where-Object { $_.browsable -and $_.handle })
            $selectedAddon = if ([string]::IsNullOrWhiteSpace($PreferredAddonName)) {
                $browsableAddons | Select-Object -First 1
            }
            else {
                $browsableAddons | Where-Object { $_.name -like "*$PreferredAddonName*" } | Select-Object -First 1
            }
            if ($null -eq $selectedAddon) { throw 'The requested browsable add-on was not present in the bounded result.' }

            $currentHandle = $selectedAddon.handle
            $completedDepth = 0
            $visitedItemCount = 0
            for ($depth = 1; $depth -le $AddonTraversalDepth; $depth++) {
                $browseCall = @{
                    jsonrpc = '2.0'
                    id = 3 + $depth
                    method = 'tools/call'
                    params = @{
                        name = 'kodi_browse'
                        arguments = @{ handle = $currentHandle; media = 'video'; page = 0; pageSize = 25 }
                    }
                } | ConvertTo-Json -Depth 6 -Compress
                $browseResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                    -Method Post -ContentType 'application/json' -Headers $headers -Body $browseCall -TimeoutSec 30
                $browseDataLine = $browseResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
                $browseJson = if ($browseDataLine) { $browseDataLine.Substring(5).Trim() } else { $browseResponse.Content }
                $browsePayload = $browseJson | ConvertFrom-Json
                if ($browsePayload.error -or $browsePayload.result.isError) { break }
                $page = $browsePayload.result.content[0].text | ConvertFrom-Json
                $items = @($page.items)
                $visitedItemCount += $items.Count
                $completedDepth = $depth
                $next = $items | Where-Object { $_.handle -and @($_.availableActions) -contains 'browse' } | Select-Object -First 1
                if ($null -eq $next) { break }
                $currentHandle = $next.handle
            }
            $summary.AddonTraversalRequestedDepth = $AddonTraversalDepth
            $summary.AddonTraversalCompletedDepth = $completedDepth
            $summary.AddonTraversalVisitedItemCount = $visitedItemCount
        }

        if ($AddonMenuPath.Count -gt 0) {
            if ([string]::IsNullOrWhiteSpace($PreferredAddonName)) {
                throw 'A preferred add-on name is required for semantic menu-path probing.'
            }
            $selectedAddon = @($addonPage.addons | Where-Object { $_.browsable -and $_.handle }) |
                Where-Object { $_.name -like "*$PreferredAddonName*" } | Select-Object -First 1
            if ($null -eq $selectedAddon) { throw 'The requested browsable add-on was not present in the bounded result.' }

            $currentHandle = $selectedAddon.handle
            $matchedSegments = 0
            for ($segmentIndex = 0; $segmentIndex -lt $AddonMenuPath.Count; $segmentIndex++) {
                $menuCall = @{
                    jsonrpc = '2.0'
                    id = 20 + $segmentIndex
                    method = 'tools/call'
                    params = @{
                        name = 'kodi_browse'
                        arguments = @{ handle = $currentHandle; media = 'video'; page = 0; pageSize = 50 }
                    }
                } | ConvertTo-Json -Depth 6 -Compress
                $menuResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                    -Method Post -ContentType 'application/json' -Headers $headers -Body $menuCall -TimeoutSec 30
                $menuDataLine = $menuResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
                $menuJson = if ($menuDataLine) { $menuDataLine.Substring(5).Trim() } else { $menuResponse.Content }
                $menuPayload = $menuJson | ConvertFrom-Json
                if ($menuPayload.error -or $menuPayload.result.isError) { throw 'An add-on menu page could not be read.' }
                $menuPage = $menuPayload.result.content[0].text | ConvertFrom-Json
                $segment = $AddonMenuPath[$segmentIndex]
                $items = @($menuPage.items | Where-Object { $_.handle })
                $selectedItem = $items | Where-Object { $_.label -eq $segment } | Select-Object -First 1
                if ($null -eq $selectedItem) {
                    $selectedItem = $items | Where-Object { $_.label -like "*$segment*" } | Select-Object -First 1
                }
                if ($null -eq $selectedItem) { throw 'A requested semantic menu segment was not found.' }
                $currentHandle = $selectedItem.handle
                $matchedSegments++
            }
            $summary.AddonMenuSegmentsRequested = $AddonMenuPath.Count
            $summary.AddonMenuSegmentsMatched = $matchedSegments
        }
    }

    [pscustomobject]$summary
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id
        Wait-Process -Id $serverProcess.Id -Timeout 5 -ErrorAction SilentlyContinue
    }
}
