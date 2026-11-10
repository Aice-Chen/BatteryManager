[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,64}$')][string]$PipeSuffix,
    [string]$ExpectedFont,
    [switch]$RequireBundledFont
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskBefore = & $taskClient -PipeSuffix $PipeSuffix -Command status
if ($null -eq $taskBefore.status.scenario -or $taskBefore.status.scenario.usesRealHardware -or $taskBefore.status.scenario.savesPreferences) {
    throw 'Tray glyph export requires an isolated, memory-settings scenario process.'
}
if ([string]::IsNullOrEmpty($taskBefore.status.diagnosticLog)) { throw 'The scenario process did not expose its log directory.' }
$taskLog = Resolve-PortableProjectPath $taskBefore.status.diagnosticLog $taskRoot -ArtifactsOnly
$taskDirectory = Resolve-PortableProjectPath (Join-Path (Split-Path -Parent $taskLog) 'tray-glyphs') $taskRoot -ArtifactsOnly
$taskAfter = & $taskClient -PipeSuffix $PipeSuffix -Command ui -Action 'capture-tray-glyphs' -ResponseTimeoutMilliseconds 60000
if ($taskAfter.status.scenario.usesRealHardware -or $taskAfter.status.scenario.savesPreferences -or
    $taskAfter.status.session.generation -ne $taskBefore.status.session.generation -or
    $taskAfter.status.scenario.openedHidChannels -ne $taskBefore.status.scenario.openedHidChannels) {
    throw 'Export unexpectedly changed the scenario collection or isolation state.'
}
$taskMetrics = Get-Content -LiteralPath (Join-Path $taskDirectory 'metrics.json') -Raw | ConvertFrom-Json -Depth 20
if ($taskMetrics.schemaVersion -ne 1 -or $taskMetrics.samples.Count -ne 20) { throw 'Unexpected glyph matrix.' }
if ($ExpectedFont -and ($taskAfter.status.tray.glyphFace -cne $ExpectedFont -or $taskMetrics.renderer -cne ('Production TrayNumber.Draw / ' + $ExpectedFont))) {
    throw "The actual tray font differs from the requested font: $($taskAfter.status.tray.glyphFace). See metrics.json for rendering evidence."
}
if ($RequireBundledFont -and (-not $taskAfter.status.tray.bundledFontLoaded -or -not $taskMetrics.bundledFontLoaded)) {
    throw 'The bundled font was not loaded by the actual tray renderer.'
}
Add-Type -AssemblyName System.Drawing
$taskSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$taskResults = foreach ($taskSample in $taskMetrics.samples) {
    $taskSize = [int]$taskSample.size
    $taskText = [string]$taskSample.text
    if ($taskSize -notin @(16, 20, 24, 28, 32) -or $taskText -notin @('8', '73', '100', '—') -or
        -not $taskSeen.Add("$taskSize/$taskText")) { throw 'Unexpected or duplicated glyph sample.' }
    $taskExpectedName = [string]$taskSize + '-' + $(if ($taskText -eq '—') { 'unknown' } else { $taskText }) + '.png'
    if ($taskSample.file -cne $taskExpectedName) { throw 'Unexpected glyph filename.' }
    $taskPath = Resolve-PortableProjectPath (Join-Path $taskDirectory $taskExpectedName) $taskRoot -ArtifactsOnly
    $taskBitmap = [Drawing.Bitmap]::new($taskPath)
    try {
        if ($taskBitmap.Width -ne $taskSize -or $taskBitmap.Height -ne $taskSize) { throw "Wrong PNG size: $taskExpectedName" }
        $taskCoverage = [byte[]]::new($taskSize * $taskSize)
        $taskLeft = $taskSize; $taskTop = $taskSize; $taskRight = -1; $taskBottom = -1
        $taskNonZero = 0; $taskPartial = 0; $taskMaximum = 0; $taskMinimum = 255
        for ($taskY = 0; $taskY -lt $taskSize; $taskY++) {
            for ($taskX = 0; $taskX -lt $taskSize; $taskX++) {
                $taskAlpha = [int]$taskBitmap.GetPixel($taskX, $taskY).A
                $taskCoverage[$taskY * $taskSize + $taskX] = [byte]$taskAlpha
                $taskMinimum = [Math]::Min($taskMinimum, $taskAlpha); $taskMaximum = [Math]::Max($taskMaximum, $taskAlpha)
                if ($taskAlpha -eq 0) { continue }
                $taskNonZero++
                if ($taskAlpha -lt 255) { $taskPartial++ }
                $taskLeft = [Math]::Min($taskLeft, $taskX); $taskRight = [Math]::Max($taskRight, $taskX)
                $taskTop = [Math]::Min($taskTop, $taskY); $taskBottom = [Math]::Max($taskBottom, $taskY)
            }
        }
        # A horizontal unknown mark can land exactly on pixel boundaries, so a
        # crisp solid line legitimately has no partial-alpha edge pixels.
        if ($taskNonZero -eq 0 -or $taskMinimum -ne 0 -or $taskMaximum -lt 128 -or ($taskText -ne '—' -and $taskPartial -eq 0)) {
            throw "The glyph lacks visible/transparent pixels or a numeric glyph lacks antialiased edges: $taskExpectedName"
        }
        $taskInkWidth = $taskRight - $taskLeft + 1; $taskInkHeight = $taskBottom - $taskTop + 1
        if ($taskTop -lt 1 -or $taskBottom -ge $taskSize - 1) { throw "The glyph touches a vertical image boundary: $taskExpectedName" }
        if ($taskText -in @('8', '73') -and $taskInkHeight -lt 0.70 * $taskSize) { throw "The single/two-digit glyph is too short: $taskExpectedName" }
        if ($taskText -eq '100' -and $taskInkHeight -lt 0.50 * $taskSize) { throw "The three-digit glyph is too short: $taskExpectedName" }
        if ($taskText -eq '—' -and ($taskInkWidth -lt 0.50 * $taskSize -or $taskInkWidth -le $taskInkHeight)) { throw "Unknown glyph is not a legible horizontal mark: $taskExpectedName" }
        if ($taskText -eq '—' -and ($taskLeft -ne $taskSize-1-$taskRight -or $taskTop -ne $taskSize-1-$taskBottom)) { throw "Unknown glyph margins are not symmetric: $taskExpectedName" }
        if ($taskSample.reportedWidth -gt $taskSize -or $taskSample.reportedHeight -gt $taskSize -or
            $taskSample.reportedWidth -lt $taskInkWidth -or $taskSample.reportedHeight -lt $taskInkHeight) {
            throw "Reported glyph dimensions do not contain its actual ink: $taskExpectedName"
        }
        $taskSha = [Security.Cryptography.SHA256]::Create()
        try { $taskHash = ([BitConverter]::ToString($taskSha.ComputeHash($taskCoverage))).Replace('-', '').ToLowerInvariant() }
        finally { $taskSha.Dispose() }
        if ($taskHash -cne $taskSample.coverageSha256 -or $taskSample.ink.left -ne $taskLeft -or $taskSample.ink.top -ne $taskTop -or
            $taskSample.ink.width -ne $taskInkWidth -or $taskSample.ink.height -ne $taskInkHeight -or
            $taskSample.alphaMinimum -ne $taskMinimum -or $taskSample.alphaMaximum -ne $taskMaximum -or
            $taskSample.nonZeroPixels -ne $taskNonZero -or $taskSample.partialAlphaPixels -ne $taskPartial) {
            throw "PNG pixels disagree with the exported renderer evidence: $taskExpectedName"
        }
        [pscustomobject]@{ size=$taskSize; text=$taskText; width=$taskInkWidth; height=$taskInkHeight; alphaMaximum=$taskMaximum; partialAlphaPixels=$taskPartial }
    } finally { $taskBitmap.Dispose() }
}
$taskResult = [pscustomobject]@{
    passed=$true; samples=$taskResults.Count; actualShellPresentationVerified=$false
    renderer=$taskMetrics.renderer
    glyphFace=$taskAfter.status.tray.glyphFace
    bundledFontLoaded=$(if ($taskMetrics.PSObject.Properties['bundledFontLoaded']) { $taskMetrics.bundledFontLoaded } else { $null })
    note='Production renderer at requested pixel sizes; Windows scaling and Shell presentation were not changed.'
    results=$taskResults
}
$taskResult | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskDirectory 'verification.json') -Encoding utf8
[pscustomobject]@{ passed=$true; samples=$taskResults.Count; evidenceDirectory=$taskDirectory }
