[CmdletBinding()]
param(
    [string]$Executable = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\win-x64\KodiMCPSharp.exe'),
    [ValidateRange(1024, 65535)]
    [int]$Port = 58080,
    [ValidateRange(1, 1000)]
    [int]$ExpectedToolCount = 53,
    [switch]$ProbeAddons,
    [switch]$AllowLocalConfiguration,
    [ValidateRange(0, 3)]
    [int]$AddonTraversalDepth = 0,
    [ValidateLength(0, 200)]
    [string]$PreferredAddonName = '',
    [string[]]$AddonMenuPath = @(),
    [switch]$CaptureCurrentAddonPage,
    [ValidateLength(0, 100)]
    [string]$CapturedRouteName = '',
    [ValidateLength(0, 200)]
    [string]$CapturedRouteSampleValue = '',
    [ValidateLength(0, 200)]
    [string]$CapturedRouteTestValue = '',
    [switch]$ForgetCapturedRoute,
    [switch]$EnableLearnedRouteWritesForTest,
    [ValidateLength(0, 100)]
    [string]$ExistingRouteName = '',
    [ValidateLength(0, 200)]
    [string]$ExistingRouteTestValue = '',
    [switch]$ForgetExistingRoute,
    [switch]$ProbeQueues,
    [switch]$ProbeUpNext,
    [switch]$ProbeVideoMetadata,
    [switch]$ProbeMusicHistory,
    [switch]$ProbeRouteHealth,
    [switch]$ProbePvr
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path $Executable).Path
$serverArguments = @("--Server:Port=$Port")
if ($EnableLearnedRouteWritesForTest) {
    $serverArguments += '--Kodi:LearnedRoutes:AllowWrite=true'
}
$serverProcess = Start-Process -FilePath $resolvedExecutable `
    -ArgumentList $serverArguments `
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
    function Invoke-SmokeTool([int]$Id, [string]$Name, [hashtable]$Arguments) {
        $call = @{ jsonrpc = '2.0'; id = $Id; method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } } |
            ConvertTo-Json -Depth 8 -Compress
        $response = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
            -Method Post -ContentType 'application/json' -Headers $headers -Body $call -TimeoutSec 30
        $responseDataLine = $response.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
        $responseJson = if ($responseDataLine) { $responseDataLine.Substring(5).Trim() } else { $response.Content }
        $responsePayload = $responseJson | ConvertFrom-Json
        if ($responsePayload.error -or $responsePayload.result.isError) { throw "$Name failed." }
        return $responsePayload.result.content[0].text | ConvertFrom-Json
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
    if (-not ($tools | Where-Object name -eq 'kodi_capture_current_addon_page')) { throw 'Packaged server is missing kodi_capture_current_addon_page.' }
    if (-not ($tools | Where-Object name -eq 'kodi_list_recently_watched_shows')) { throw 'Packaged server is missing kodi_list_recently_watched_shows.' }
    if (-not ($tools | Where-Object name -eq 'kodi_list_recently_watched_movies')) { throw 'Packaged server is missing kodi_list_recently_watched_movies.' }
    if (-not ($tools | Where-Object name -eq 'kodi_get_queue')) { throw 'Packaged server is missing kodi_get_queue.' }
    if (-not ($tools | Where-Object name -eq 'kodi_move_queue_item')) { throw 'Packaged server is missing kodi_move_queue_item.' }
    if (-not ($tools | Where-Object name -eq 'kodi_list_up_next')) { throw 'Packaged server is missing kodi_list_up_next.' }
    if (-not ($tools | Where-Object name -eq 'kodi_play_random')) { throw 'Packaged server is missing kodi_play_random.' }
    if (-not ($tools | Where-Object name -eq 'kodi_list_movie_sets')) { throw 'Packaged server is missing kodi_list_movie_sets.' }
    if (-not ($tools | Where-Object name -eq 'kodi_browse_movie_set')) { throw 'Packaged server is missing kodi_browse_movie_set.' }
    if (-not ($tools | Where-Object name -eq 'kodi_list_video_tags')) { throw 'Packaged server is missing kodi_list_video_tags.' }
    if (-not ($tools | Where-Object name -eq 'kodi_get_video_details')) { throw 'Packaged server is missing kodi_get_video_details.' }
    if (-not ($tools | Where-Object name -eq 'kodi_list_recently_played_music')) { throw 'Packaged server is missing kodi_list_recently_played_music.' }
    if (-not ($tools | Where-Object name -eq 'kodi_play_music')) { throw 'Packaged server is missing kodi_play_music.' }
    if (-not ($tools | Where-Object name -eq 'kodi_check_addon_routes')) { throw 'Packaged server is missing kodi_check_addon_routes.' }
    if (-not ($tools | Where-Object name -eq 'kodi_library_maintenance')) { throw 'Packaged server is missing kodi_library_maintenance.' }
    if (-not ($tools | Where-Object name -eq 'kodi_list_pvr_channels')) { throw 'Packaged server is missing kodi_list_pvr_channels.' }
    if (-not ($tools | Where-Object name -eq 'kodi_list_pvr_recordings')) { throw 'Packaged server is missing kodi_list_pvr_recordings.' }
    if (-not ($tools | Where-Object name -eq 'kodi_list_pvr_timers')) { throw 'Packaged server is missing kodi_list_pvr_timers.' }
    if (-not ($tools | Where-Object name -eq 'kodi_play_pvr')) { throw 'Packaged server is missing kodi_play_pvr.' }
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
    if ($CaptureCurrentAddonPage) {
        if ($health.configuredInstances -lt 1) { throw 'Current-page capture requires a configured instance.' }
        $captureCall = @{
            jsonrpc = '2.0'
            id = 80
            method = 'tools/call'
            params = @{ name = 'kodi_capture_current_addon_page'; arguments = @{} }
        } | ConvertTo-Json -Depth 5 -Compress
        $captureResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
            -Method Post -ContentType 'application/json' -Headers $headers -Body $captureCall -TimeoutSec 20
        $captureDataLine = $captureResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
        $captureJson = if ($captureDataLine) { $captureDataLine.Substring(5).Trim() } else { $captureResponse.Content }
        $capturePayload = $captureJson | ConvertFrom-Json
        if ($capturePayload.error) { throw "Current-page capture failed: $($capturePayload.error.message)" }
        if ($capturePayload.result.isError) {
            throw "Current-page capture failed: $($capturePayload.result.content[0].text)"
        }
        $capturedPage = $capturePayload.result.content[0].text | ConvertFrom-Json
        $summary.CurrentAddonPageCaptured = [bool]($capturedPage.canBrowse -and $capturedPage.handle)

        if (-not [string]::IsNullOrWhiteSpace($CapturedRouteName)) {
            $saveArguments = @{ handle = $capturedPage.handle; name = $CapturedRouteName }
            if (-not [string]::IsNullOrWhiteSpace($CapturedRouteSampleValue)) {
                $saveArguments.sampleValue = $CapturedRouteSampleValue
            }
            $saveCall = @{
                jsonrpc = '2.0'
                id = 81
                method = 'tools/call'
                params = @{ name = 'kodi_save_addon_route'; arguments = $saveArguments }
            } | ConvertTo-Json -Depth 6 -Compress
            $saveResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                -Method Post -ContentType 'application/json' -Headers $headers -Body $saveCall -TimeoutSec 20
            $saveDataLine = $saveResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
            $saveJson = if ($saveDataLine) { $saveDataLine.Substring(5).Trim() } else { $saveResponse.Content }
            $savePayload = $saveJson | ConvertFrom-Json
            if ($savePayload.error) { throw "Captured-route save failed: $($savePayload.error.message)" }
            if ($savePayload.result.isError) { throw "Captured-route save failed: $($savePayload.result.content[0].text)" }
            $savedRoute = $savePayload.result.content[0].text | ConvertFrom-Json
            $summary.CapturedRouteSaved = [bool]$savedRoute.handle
            $summary.CapturedRouteRequiresInput = [bool]$savedRoute.requiresInput

            if (-not [string]::IsNullOrWhiteSpace($CapturedRouteTestValue)) {
                $bindCall = @{
                    jsonrpc = '2.0'
                    id = 82
                    method = 'tools/call'
                    params = @{
                        name = 'kodi_bind_addon_route'
                        arguments = @{ handle = $savedRoute.handle; input = $CapturedRouteTestValue }
                    }
                } | ConvertTo-Json -Depth 6 -Compress
                $bindResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                    -Method Post -ContentType 'application/json' -Headers $headers -Body $bindCall -TimeoutSec 20
                $bindDataLine = $bindResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
                $bindJson = if ($bindDataLine) { $bindDataLine.Substring(5).Trim() } else { $bindResponse.Content }
                $bindPayload = $bindJson | ConvertFrom-Json
                if ($bindPayload.error -or $bindPayload.result.isError) { throw 'Captured-route binding failed.' }
                $boundRoute = $bindPayload.result.content[0].text | ConvertFrom-Json

                $boundBrowseCall = @{
                    jsonrpc = '2.0'
                    id = 83
                    method = 'tools/call'
                    params = @{
                        name = 'kodi_browse'
                        arguments = @{ handle = $boundRoute.handle; root = 'video'; page = 0; pageSize = 5 }
                    }
                } | ConvertTo-Json -Depth 6 -Compress
                $boundBrowseResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                    -Method Post -ContentType 'application/json' -Headers $headers -Body $boundBrowseCall -TimeoutSec 30
                $boundBrowseDataLine = $boundBrowseResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
                $boundBrowseJson = if ($boundBrowseDataLine) { $boundBrowseDataLine.Substring(5).Trim() } else { $boundBrowseResponse.Content }
                $boundBrowsePayload = $boundBrowseJson | ConvertFrom-Json
                if ($boundBrowsePayload.error -or $boundBrowsePayload.result.isError) { throw 'Captured-route browse verification failed.' }
                $summary.CapturedRouteBoundAndBrowsed = $true
            }

            if ($ForgetCapturedRoute) {
                $forgetCall = @{
                    jsonrpc = '2.0'
                    id = 84
                    method = 'tools/call'
                    params = @{ name = 'kodi_forget_addon_route'; arguments = @{ handle = $savedRoute.handle } }
                } | ConvertTo-Json -Depth 6 -Compress
                $forgetResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                    -Method Post -ContentType 'application/json' -Headers $headers -Body $forgetCall -TimeoutSec 20
                $forgetDataLine = $forgetResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
                $forgetJson = if ($forgetDataLine) { $forgetDataLine.Substring(5).Trim() } else { $forgetResponse.Content }
                $forgetPayload = $forgetJson | ConvertFrom-Json
                if ($forgetPayload.error -or $forgetPayload.result.isError) { throw 'Captured-route cleanup failed.' }
                $forgottenRoute = $forgetPayload.result.content[0].text | ConvertFrom-Json
                $summary.CapturedRouteRemoved = [bool]$forgottenRoute.removed
            }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($ExistingRouteName)) {
        $listRoutesCall = @{
            jsonrpc = '2.0'
            id = 90
            method = 'tools/call'
            params = @{ name = 'kodi_list_addon_routes'; arguments = @{} }
        } | ConvertTo-Json -Depth 5 -Compress
        $listRoutesResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
            -Method Post -ContentType 'application/json' -Headers $headers -Body $listRoutesCall -TimeoutSec 20
        $listRoutesDataLine = $listRoutesResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
        $listRoutesJson = if ($listRoutesDataLine) { $listRoutesDataLine.Substring(5).Trim() } else { $listRoutesResponse.Content }
        $listRoutesPayload = $listRoutesJson | ConvertFrom-Json
        if ($listRoutesPayload.error -or $listRoutesPayload.result.isError) { throw 'Persistent-route listing failed.' }
        $routePage = $listRoutesPayload.result.content[0].text | ConvertFrom-Json
        $existingRoute = @($routePage.routes | Where-Object { $_.name -eq $ExistingRouteName }) | Select-Object -First 1
        if ($null -eq $existingRoute) { throw 'The expected persistent route was not reloaded.' }
        $summary.ExistingRouteReloaded = $true

        if (-not [string]::IsNullOrWhiteSpace($ExistingRouteTestValue)) {
            $existingBindCall = @{
                jsonrpc = '2.0'
                id = 91
                method = 'tools/call'
                params = @{
                    name = 'kodi_bind_addon_route'
                    arguments = @{ handle = $existingRoute.handle; input = $ExistingRouteTestValue }
                }
            } | ConvertTo-Json -Depth 6 -Compress
            $existingBindResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                -Method Post -ContentType 'application/json' -Headers $headers -Body $existingBindCall -TimeoutSec 20
            $existingBindDataLine = $existingBindResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
            $existingBindJson = if ($existingBindDataLine) { $existingBindDataLine.Substring(5).Trim() } else { $existingBindResponse.Content }
            $existingBindPayload = $existingBindJson | ConvertFrom-Json
            if ($existingBindPayload.error -or $existingBindPayload.result.isError) { throw 'Persistent-route binding failed.' }
            $existingBoundRoute = $existingBindPayload.result.content[0].text | ConvertFrom-Json

            $existingBrowseCall = @{
                jsonrpc = '2.0'
                id = 92
                method = 'tools/call'
                params = @{
                    name = 'kodi_browse'
                    arguments = @{ handle = $existingBoundRoute.handle; root = 'video'; page = 0; pageSize = 5 }
                }
            } | ConvertTo-Json -Depth 6 -Compress
            $existingBrowseResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                -Method Post -ContentType 'application/json' -Headers $headers -Body $existingBrowseCall -TimeoutSec 30
            $existingBrowseDataLine = $existingBrowseResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
            $existingBrowseJson = if ($existingBrowseDataLine) { $existingBrowseDataLine.Substring(5).Trim() } else { $existingBrowseResponse.Content }
            $existingBrowsePayload = $existingBrowseJson | ConvertFrom-Json
            if ($existingBrowsePayload.error -or $existingBrowsePayload.result.isError) { throw 'Persistent-route browse verification failed.' }
            $summary.ExistingRouteBoundAndBrowsed = $true
        }

        if ($ForgetExistingRoute) {
            $existingForgetCall = @{
                jsonrpc = '2.0'
                id = 93
                method = 'tools/call'
                params = @{ name = 'kodi_forget_addon_route'; arguments = @{ handle = $existingRoute.handle } }
            } | ConvertTo-Json -Depth 6 -Compress
            $existingForgetResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                -Method Post -ContentType 'application/json' -Headers $headers -Body $existingForgetCall -TimeoutSec 20
            $existingForgetDataLine = $existingForgetResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
            $existingForgetJson = if ($existingForgetDataLine) { $existingForgetDataLine.Substring(5).Trim() } else { $existingForgetResponse.Content }
            $existingForgetPayload = $existingForgetJson | ConvertFrom-Json
            if ($existingForgetPayload.error -or $existingForgetPayload.result.isError) { throw 'Persistent-route cleanup failed.' }
            $existingForgottenRoute = $existingForgetPayload.result.content[0].text | ConvertFrom-Json
            $summary.ExistingRouteRemoved = [bool]$existingForgottenRoute.removed
        }
    }
    if ($ProbeQueues) {
        if ($health.configuredInstances -lt 1) { throw 'Queue probing requires a configured instance.' }
        foreach ($queueMedia in @('audio', 'video')) {
            $queueCall = @{
                jsonrpc = '2.0'
                id = if ($queueMedia -eq 'audio') { 100 } else { 101 }
                method = 'tools/call'
                params = @{ name = 'kodi_get_queue'; arguments = @{ media = $queueMedia; page = 0; pageSize = 10 } }
            } | ConvertTo-Json -Depth 6 -Compress
            $queueResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
                -Method Post -ContentType 'application/json' -Headers $headers -Body $queueCall -TimeoutSec 20
            $queueDataLine = $queueResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
            $queueJson = if ($queueDataLine) { $queueDataLine.Substring(5).Trim() } else { $queueResponse.Content }
            $queuePayload = $queueJson | ConvertFrom-Json
            if ($queuePayload.error -or $queuePayload.result.isError) { throw "$queueMedia queue inspection failed." }
            $queuePage = $queuePayload.result.content[0].text | ConvertFrom-Json
            $summary["$($queueMedia)QueueTotal"] = $queuePage.total
            $summary["$($queueMedia)QueueReturned"] = @($queuePage.items).Count
        }
    }
    if ($ProbeUpNext) {
        if ($health.configuredInstances -lt 1) { throw 'Up-next probing requires a configured instance.' }
        $upNextCall = @{
            jsonrpc = '2.0'
            id = 110
            method = 'tools/call'
            params = @{ name = 'kodi_list_up_next'; arguments = @{ limit = 10 } }
        } | ConvertTo-Json -Depth 6 -Compress
        $upNextResponse = Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/mcp" `
            -Method Post -ContentType 'application/json' -Headers $headers -Body $upNextCall -TimeoutSec 30
        $upNextDataLine = $upNextResponse.Content -split "`n" | Where-Object { $_ -like 'data:*' } | Select-Object -Last 1
        $upNextJson = if ($upNextDataLine) { $upNextDataLine.Substring(5).Trim() } else { $upNextResponse.Content }
        $upNextPayload = $upNextJson | ConvertFrom-Json
        if ($upNextPayload.error -or $upNextPayload.result.isError) { throw 'Up-next inspection failed.' }
        $upNextResult = $upNextPayload.result.content[0].text | ConvertFrom-Json
        $summary.UpNextReturned = $upNextResult.returned
        $summary.UpNextScannedEpisodes = $upNextResult.scannedEpisodes
    }
    if ($ProbeVideoMetadata) {
        if ($health.configuredInstances -lt 1) { throw 'Video metadata probing requires a configured instance.' }
        $tagResult = Invoke-SmokeTool 120 'kodi_list_video_tags' @{ domain = 'movies'; page = 0; pageSize = 10 }
        $setResult = Invoke-SmokeTool 121 'kodi_list_movie_sets' @{ page = 0; pageSize = 10 }
        $summary.VideoTagTotal = $tagResult.total
        $summary.MovieSetTotal = $setResult.total
        $summary.MovieSetBrowseReturned = 0
        if (@($setResult.sets).Count -gt 0) {
            $setMovies = Invoke-SmokeTool 122 'kodi_browse_movie_set' @{ handle = $setResult.sets[0].handle; page = 0; pageSize = 10 }
            $summary.MovieSetBrowseReturned = @($setMovies.items).Count
        }
        $recentMovies = Invoke-SmokeTool 123 'kodi_list_recent' @{ domain = 'movies'; page = 0; pageSize = 1 }
        $summary.VideoDetailsObserved = $false
        if (@($recentMovies.items).Count -gt 0 -and -not [string]::IsNullOrWhiteSpace($recentMovies.items[0].label)) {
            $detailArguments = @{ domain = 'movies'; title = $recentMovies.items[0].label }
            if ($null -ne $recentMovies.items[0].year) { $detailArguments.year = $recentMovies.items[0].year }
            $videoDetails = Invoke-SmokeTool 124 'kodi_get_video_details' $detailArguments
            $summary.VideoDetailsObserved = ($videoDetails.domain -eq 'movies')
        }
    }
    if ($ProbeMusicHistory) {
        if ($health.configuredInstances -lt 1) { throw 'Music history probing requires a configured instance.' }
        $recentSongs = Invoke-SmokeTool 130 'kodi_list_recently_played_music' @{ domain = 'songs'; page = 0; pageSize = 10 }
        $recentAlbums = Invoke-SmokeTool 131 'kodi_list_recently_played_music' @{ domain = 'albums'; page = 0; pageSize = 10 }
        $summary.RecentSongTotal = $recentSongs.total
        $summary.RecentAlbumTotal = $recentAlbums.total
    }
    if ($ProbeRouteHealth) {
        if ($health.configuredInstances -lt 1) { throw 'Route health probing requires a configured instance.' }
        $routeHealth = Invoke-SmokeTool 140 'kodi_check_addon_routes' @{ probeFixedBrowseRoutes = $true }
        $summary.LearnedRouteTotal = $routeHealth.total
        $summary.LearnedRouteReachable = $routeHealth.reachable
        $summary.LearnedRouteUnavailable = $routeHealth.unavailable
    }
    if ($ProbePvr) {
        if ($health.configuredInstances -lt 1) { throw 'PVR probing requires a configured instance.' }
        $pvrChannels = Invoke-SmokeTool 150 'kodi_list_pvr_channels' @{ channelType = 'tv'; page = 0; pageSize = 10 }
        $pvrRecordings = Invoke-SmokeTool 151 'kodi_list_pvr_recordings' @{ page = 0; pageSize = 10 }
        $pvrTimers = Invoke-SmokeTool 152 'kodi_list_pvr_timers' @{ page = 0; pageSize = 10 }
        $summary.PvrChannelTotal = $pvrChannels.total
        $summary.PvrRecordingTotal = $pvrRecordings.total
        $summary.PvrTimerTotal = $pvrTimers.total
    }

    [pscustomobject]$summary
}
finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id
        Wait-Process -Id $serverProcess.Id -Timeout 5 -ErrorAction SilentlyContinue
    }
}
