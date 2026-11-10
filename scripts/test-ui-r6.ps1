[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe, [switch]$CaptureNativePixels)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$taskRoot=[IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\','/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe=Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')){throw 'Use an isolated publish directory.'}
if(@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count){throw 'Exit the existing app before this test.'}
$taskSuffix='r6-'+[Guid]::NewGuid().ToString('N')
$taskDirectory=Join-Path $taskRoot ('.artifacts/app-tests/'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$taskSuffix)
$null=New-Item -ItemType Directory -Path $taskDirectory
$taskSettings=Join-Path (Split-Path $taskExe) 'settings.json'
if(-not (Test-Path -LiteralPath $taskSettings)){[IO.File]::WriteAllText($taskSettings,'{"invalid-isolation-sentinel":true}')}
$taskHash=(Get-FileHash -LiteralPath $taskSettings).Hash
$taskClient=Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskResult=[ordered]@{passed=$false;hardwareMode=$false;checks=@();evidenceDirectory=$taskDirectory;scope='r6 regression plus r7 glyph and copy-action layout';actualShellPresentationVerified=$false;multipleDisplayScalesVerified=$false;imageEvidenceKind='WinUI RenderTargetBitmap actual-control exports; transparent backgrounds, without native window decoration.';glyphLayoutEvidence=@()}
$taskProcess=$null
function Command-R6([string]$Command,[string]$Action='',[string]$Value='',[string]$DeviceId=''){
 $arguments=@{PipeSuffix=$taskSuffix;Command=$Command;ConnectTimeoutMilliseconds=500;ResponseTimeoutMilliseconds=20000}
 if($Action){$arguments.Action=$Action};if($Value){$arguments.Value=$Value};if($DeviceId){$arguments.DeviceId=$DeviceId}
 & $taskClient @arguments
}
function Ui-R6([string]$Action,[string]$Value='',[string]$DeviceId=''){Command-R6 'ui' $Action $Value $DeviceId}
function Save-R6([string]$Name,$Reply){$Reply | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name+'.json')) -Encoding utf8}
function Check-R6([bool]$Condition,[string]$Name){
 if(-not $Condition){
  if($null -ne $taskProcess -and -not $taskProcess.HasExited){try{Save-R6 'failed-state' (Command-R6 'status')}catch{}}
  throw $Name
 }
 $taskResult.checks+=$Name
}
function Wait-R6([scriptblock]$Condition){
 $deadline=[Diagnostics.Stopwatch]::StartNew();$last=$null
 while($deadline.Elapsed.TotalSeconds -lt 15){
  if($taskProcess.HasExited){throw 'The application exited unexpectedly.'}
  try{$last=Command-R6 'status';if(& $Condition $last.status){return $last}}catch{if($deadline.Elapsed.TotalSeconds -ge 15){throw}}
  Start-Sleep -Milliseconds 40
 }
 if($null -ne $last){Save-R6 'last-timeout-state' $last}
 throw 'Expected r6 regression / r7 layout did not settle.'
}
function Scenario-R6([string]$Name){
 $null=Command-R6 'scenario' $Name
 Wait-R6 {param($s) $s.scenario.name -eq $Name -and -not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.session.outstandingOperations -eq 0 -and $s.view.version -eq $s.session.state.version}
}
function OpenPanel-R6([int]$Count){
 $null=Ui-R6 'panel-open'
 Start-Sleep -Milliseconds 350
 Wait-R6 {param($s) $s.panel.visible -and [Math]::Abs($s.panel.contentOffsetY) -le 0.0001 -and $s.panel.cards.Count -eq $Count -and @($s.panel.cards | Where-Object {$null -eq $_.glyphLayout -or $_.glyphLayout.rowWidth -le 0 -or $_.glyphLayout.rowWidth -gt $s.panel.width / $s.panel.rasterizationScale}).Count -eq 0}
}
function Capture-R6([string]$Action,[string]$File){
 # Static exports wait for entrance transitions; this does not measure animation performance.
 Start-Sleep -Milliseconds 1200
 $null=Ui-R6 $Action $File
}
function ReadGlyphPixels-R6([string]$Layout){
 # Read production diagnostics; do not duplicate the glyph's sizing or rounding algorithm.
 $match=[regex]::Match($Layout,'^(?<stroke>\d+),(?<gap>\d+),(?<shellWidth>\d+)x(?<shellHeight>\d+),(?<fillWidth>\d+)x(?<fillHeight>\d+),(?<shown>\d+)@(?<scale>\d+(?:\.\d+)?)$')
 if(-not $match.Success){return $null}
 [pscustomobject]@{
  stroke=[int]$match.Groups['stroke'].Value;gap=[int]$match.Groups['gap'].Value
  shellWidth=[int]$match.Groups['shellWidth'].Value;shellHeight=[int]$match.Groups['shellHeight'].Value
  fillWidth=[int]$match.Groups['fillWidth'].Value;fillHeight=[int]$match.Groups['fillHeight'].Value
  shown=[int]$match.Groups['shown'].Value;scale=[double]::Parse($match.Groups['scale'].Value,[Globalization.CultureInfo]::InvariantCulture)
 }
}
function CheckGlyphPixels-R6($Rows,[string]$Label){
 $samples=@($Rows | ForEach-Object {
  [pscustomobject]@{id=$_.id;battery=$_.battery;state=$_.batteryGlyph;steps=$_.glyphSteps;pixelLayout=$_.glyphPixelLayout;markLayout=$_.markLayout;pixels=(ReadGlyphPixels-R6 $_.glyphPixelLayout);rootBounds=$_.glyphRootBounds;rowLayout=$_.glyphLayout}
 })
 $evidenceName='glyph-layout-'+($Label -replace '[^a-zA-Z0-9-]','-')
 Save-R6 $evidenceName $samples
 $taskResult.glyphLayoutEvidence+=($evidenceName+'.json')
 $invalid=@($samples | Where-Object {
  $p=$_.pixels;$b=$_.rootBounds;$g=$_.rowLayout
  if($null -eq $p -or $null -eq $b -or $null -eq $g){return $true}
  $scale=[double]$b.rasterizationScale
  $scale -le 0 -or [Math]::Abs($p.scale-$scale) -gt 0.0001 -or [Math]::Abs($g.rasterizationScale-$scale) -gt 0.0001 -or
  $p.stroke -le 0 -or $p.gap -le 0 -or $p.fillWidth -le 0 -or $p.fillHeight -le 0 -or
  $p.fillWidth -ge $p.shellWidth -or $p.fillHeight -ge $p.shellHeight -or $p.shown -gt $p.fillWidth -or
  $b.width -le 0 -or $b.height -le 0 -or $p.shellWidth -ge $b.width*$scale -or
  [Math]::Abs($p.shellHeight-$b.height*$scale) -gt 0.02 -or
  [Math]::Abs($b.width-$g.glyphActualWidth) -gt 0.01 -or [Math]::Abs($b.width-($g.glyphRight-$g.glyphLeft)) -gt 0.01 -or
  [Math]::Abs($b.height-($g.glyphBottom-$g.glyphTop)) -gt 0.01
 })
 Check-R6 ($samples.Count -gt 0 -and $invalid.Count -eq 0) "$Label glyph pixel diagnostics match the actual XAML-root and row dimensions"
 $offPixel=@($samples | Where-Object {
  $b=$_.rootBounds;$scale=[double]$b.rasterizationScale
  @($b.left,$b.top,$b.width,$b.height | Where-Object {[Math]::Abs([double]$_*$scale-[Math]::Round([double]$_*$scale)) -gt 0.02}).Count -gt 0
 })
 Check-R6 ($offPixel.Count -eq 0) "$Label settled glyph origins and extents occupy whole pixels relative to XamlRoot.Content at the current scale"
 $marksInvalid=@($samples | Where-Object {
  [string]::IsNullOrEmpty($_.markLayout) -or
  ([Math]::Abs($_.rootBounds.rasterizationScale-1.25) -lt 0.0001 -and $_.markLayout -cne 'bolt bar 2; question stroke 2, hook radius 2x2, clear 1, dot gap 1')
 })
 Check-R6 ($marksInvalid.Count -eq 0) "$Label reports live bolt/question geometry, including the agreed 125-percent mark dimensions when applicable"
}
function CheckBounds-R6($Rows,[string]$Label,[double]$Height){
 Check-R6 (@($Rows | Where-Object {$null -eq $_.glyphLayout -or [Math]::Abs($_.headerHeight-$Height) -gt 0.01}).Count -eq 0) "$Label exposes the expected actual row height and glyph layout"
 $invalid=@($Rows | Where-Object {
  $g=$_.glyphLayout
  $g.glyphRight -le $g.glyphLeft -or $g.glyphBottom -le $g.glyphTop -or
  $g.glyphLeft -lt -0.01 -or $g.glyphRight -gt $g.rowWidth+0.01 -or $g.glyphTop -lt -0.01 -or $g.glyphBottom -gt $g.rowHeight+0.01 -or
  $g.valueLeft -lt -0.01 -or $g.valueRight -gt $g.glyphLeft -or $g.valueTop -lt -0.01 -or $g.valueBottom -gt $g.rowHeight+0.01
 })
 Check-R6 ($invalid.Count -eq 0) "$Label actual value and battery-glyph bounds fit the row without overlap"
 # ActualWidth is the TextBlock control extent, not the space assigned by layout.
 # Measure the design gap between GetLayoutSlot bounds; retain control-bound and
 # text-demand checks separately so a correct slot gap cannot hide clipped text.
 $invalidSlots=@($Rows | Where-Object {
  $g=$_.glyphLayout
  -not $g.layoutSlotsAvailable -or $g.valueSlotRight -le $g.valueSlotLeft -or $g.glyphSlotRight -le $g.glyphSlotLeft -or
  $g.valueLeft -lt $g.valueSlotLeft-0.01 -or $g.valueRight -gt $g.valueSlotRight+0.01 -or
  $g.glyphLeft -lt $g.glyphSlotLeft-0.01 -or $g.glyphRight -gt $g.glyphSlotRight+0.01 -or
  [Math]::Abs(($g.glyphSlotLeft-$g.valueSlotRight)-8) -gt 0.01
 })
 Check-R6 ($invalidSlots.Count -eq 0) "$Label actual layout slots provide an 8 DIP gap and contain their controls"
 $clipped=@($Rows | Where-Object {
  $g=$_.glyphLayout
  $scale=[double]$g.rasterizationScale
  $scale -le 0 -or $g.valueDesiredWidth -gt ([Math]::Ceiling(($g.valueRight-$g.valueLeft)*$scale)/$scale)+0.01
 })
 Check-R6 ($clipped.Count -eq 0) "$Label actual text demand fits its pixel-rounded layout width"
 $reference=$Rows[0].glyphLayout
 Check-R6 (@($Rows | Where-Object {[Math]::Abs($_.glyphLayout.glyphLeft-$reference.glyphLeft) -gt 0.5 -or [Math]::Abs($_.glyphLayout.glyphRight-$reference.glyphRight) -gt 0.5}).Count -eq 0) "$Label battery glyphs share aligned left and right edges"
 CheckGlyphPixels-R6 $Rows $Label
}
function CheckRange-R6($Rows,[string]$Label){
 $zero=@($Rows | Where-Object battery -EQ '0%');$full=@($Rows | Where-Object battery -EQ '100%')
 $low=@($Rows | Where-Object battery -EQ '9%');$normal=@($Rows | Where-Object battery -EQ '91%');$unknown=@($Rows | Where-Object battery -EQ '—')
 Check-R6 ($zero.Count -eq 1 -and $zero[0].batteryGlyph -ceq 'Level' -and $zero[0].glyphSteps -eq 0 -and -not $zero[0].charging) "$Label zero percent is an empty level battery"
 Check-R6 ($full.Count -eq 1 -and $full[0].batteryGlyph -ceq 'Charging' -and $full[0].glyphSteps -eq 10 -and $full[0].charging) "$Label charging 100 percent uses the full charging battery glyph"
 Check-R6 ($low.Count -eq 1 -and $normal.Count -eq 1 -and $low[0].batteryGlyph -ceq 'Level' -and $low[0].glyphSteps -eq 1 -and $normal[0].glyphSteps -eq 9) "$Label one-digit and two-digit readings retain raw numbers and ten-step glyphs"
 Check-R6 ($unknown.Count -eq 1 -and $unknown[0].batteryGlyph -ceq 'NoReading' -and $unknown[0].glyphSteps -eq 0 -and -not $unknown[0].charging) "$Label unknown uses a no-reading mark and zero fill steps"
 Check-R6 (@($Rows | Where-Object {$_.barVisible -or $_.barValue -ne 0}).Count -eq 0) "$Label has no obsolete battery bars"
 Check-R6 ($low[0].batteryColor -ceq $normal[0].batteryColor -and $low[0].percentColor -ceq $normal[0].percentColor -and $low[0].batteryGlyphColor -cne $normal[0].batteryGlyphColor -and $low[0].batteryGlyphColor -cne $low[0].batteryColor) "$Label low-battery colour affects the glyph while the number and percent retain their colours"
 Check-R6 ($unknown[0].percentColor -like '#00*' -and $unknown[0].batteryGlyphColor -ceq $unknown[0].batteryColor -and $unknown[0].batteryColor -cne $normal[0].batteryColor) "$Label unknown keeps a transparent percent placeholder and matching muted number/glyph"
 $zeroPixels=ReadGlyphPixels-R6 $zero[0].glyphPixelLayout;$fullPixels=ReadGlyphPixels-R6 $full[0].glyphPixelLayout
 $lowPixels=ReadGlyphPixels-R6 $low[0].glyphPixelLayout;$normalPixels=ReadGlyphPixels-R6 $normal[0].glyphPixelLayout;$unknownPixels=ReadGlyphPixels-R6 $unknown[0].glyphPixelLayout
 Check-R6 ($zeroPixels.shown -eq 0 -and $unknownPixels.shown -eq 0 -and $fullPixels.shown -eq $fullPixels.fillWidth -and
  $lowPixels.shown -gt 0 -and $lowPixels.shown -lt $normalPixels.shown -and $normalPixels.shown -lt $fullPixels.shown) "$Label actual fill geometry distinguishes zero, no reading, one step, nine steps and full charging"
}
try{
 $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @('--scenario','panel-battery-range','--test-pipe',$taskSuffix) -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
 $ready=Wait-R6 {param($s) -not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.view.version -eq $s.session.state.version -and $s.view.cards.Count -eq 5}
 Check-R6 (-not $ready.status.scenario.usesRealHardware -and -not $ready.status.scenario.savesPreferences -and $null -eq $ready.status.preferences.warning) 'The battery-range scenario isolates hardware and settings'
 Check-R6 ((@($ready.status.view.cards.battery | Sort-Object) -join ',') -ceq (@('0%','100%','9%','91%','—' | Sort-Object) -join ',')) 'The scenario includes zero, charging 100, one digit, two digits and unknown'
 $glyphs=& (Join-Path $PSScriptRoot 'test-tray-glyphs.ps1') -PipeSuffix $taskSuffix -ExpectedFont 'Barlow Semi Condensed Medium' -RequireBundledFont
 Check-R6 $glyphs.passed 'All 20 bundled Barlow production samples pass PNG checks including symmetric unknown marks'
 $taskResult.glyphEvidence=$glyphs.evidenceDirectory
 Save-R6 'startup' $ready
 foreach($mode in @('dark','light')){
  $null=Scenario-R6 'panel-battery-range'
  $null=Ui-R6 'system-theme' ($mode+','+$mode)
  $layout=OpenPanel-R6 5
  $rows=@($layout.status.panel.cards)
  CheckBounds-R6 $rows "$mode panel" 48
  CheckBounds-R6 @($layout.status.view.cards) "$mode main" 68
  CheckRange-R6 $rows "$mode panel"
  CheckRange-R6 @($layout.status.view.cards) "$mode main"
  $expectedClient=[Math]::Round(222*$layout.status.panel.rasterizationScale)
  Check-R6 ([Math]::Abs($layout.status.panel.clientHeight-$expectedClient) -le 1 -and $layout.status.panel.scrollableHeight -gt 0) "$mode five-device panel retains three-row client height and scrolls"
  Save-R6 ($mode+'-range-top') $layout
  Capture-R6 'capture-panel' ('r6-panel-'+$mode+'-top.png')
  if($CaptureNativePixels){Capture-R6 'capture-panel-native' ('r8-panel-'+$mode+'-top-native.png')}
  $lastIds=@($rows | Select-Object -Last 2 -ExpandProperty id)
  $null=Ui-R6 'panel-scroll' 'bottom'
  $bottom=Wait-R6 {param($s) $s.panel.visible -and @($lastIds | Where-Object {$_ -notin $s.panel.fullyVisibleDeviceIds}).Count -eq 0}
  Check-R6 (@($lastIds | Where-Object {$_ -notin $bottom.status.panel.fullyVisibleDeviceIds}).Count -eq 0) "$mode fourth and fifth rows fully enter the actual scroll viewport"
  Save-R6 ($mode+'-range-bottom') $bottom
  Capture-R6 'capture-panel' ('r6-panel-'+$mode+'-bottom.png')
  if($CaptureNativePixels){Capture-R6 'capture-panel-native' ('r8-panel-'+$mode+'-bottom-native.png')}
  $null=Ui-R6 'panel-close'
  Capture-R6 'capture-main' ('r6-main-'+$mode+'-range.png')
  if($CaptureNativePixels){
   Capture-R6 'capture-main-native' ('r8-main-'+$mode+'-range-native.png')
   Capture-R6 'capture-main-cards' ('r8-cards-'+$mode+'-range.png')
   Capture-R6 'capture-main-scroll' ('r8-scroll-'+$mode+'-range.png')
   $cardId=@($layout.status.view.cards | Where-Object battery -EQ '91%')[0].id
   $null=Ui-R6 'capture-main-card' ('r8-card-'+$mode+'-range.png') $cardId
  }

  $null=Scenario-R6 'pending'
  $pending=OpenPanel-R6 3
  foreach($surface in @($pending.status.view.cards,$pending.status.panel.cards)){
   $quiet=@($surface | Where-Object connection -In @('暂无响应','Not responding'))
   Check-R6 ($quiet.Count -gt 0 -and @($quiet | Where-Object {$_.battery -cne '—' -or $_.batteryGlyph -cne 'NoResponse' -or $_.glyphSteps -ne 0 -or $_.charging}).Count -eq 0) "$mode pending surface shows the question-mark state and no old number"
   Check-R6 (@($quiet | Where-Object {$_.batteryGlyphColor -cne $_.batteryColor}).Count -eq 0) "$mode pending surface uses matching muted glyph and value colours"
  }
  CheckBounds-R6 @($pending.status.panel.cards) "$mode pending panel" 48
  Save-R6 ($mode+'-pending') $pending
  Capture-R6 'capture-panel' ('r6-panel-'+$mode+'-pending.png')
  if($CaptureNativePixels){
   Capture-R6 'capture-panel-native' ('r8-panel-'+$mode+'-pending-native.png')
   Capture-R6 'capture-main-native' ('r8-main-'+$mode+'-pending-native.png')
  }
  $null=Ui-R6 'panel-close'

  $errorState=Scenario-R6 'error-long'
  $errorId=@($errorState.status.view.cards | Where-Object readFailed)[0].id
  $null=Ui-R6 'expand' '' $errorId
  $expanded=Wait-R6 {param($s) $card=@($s.view.cards | Where-Object id -EQ $errorId)[0];$card.expanded -and -not $card.isAnimating -and @($card.sourceLayouts | Where-Object {$_.rowWidth -gt 0}).Count -gt 0}
  $errorCard=@($expanded.status.view.cards | Where-Object id -EQ $errorId)[0]
  $errorRows=@($errorCard.sourceLayouts | Where-Object hasError)
  Check-R6 ($errorRows.Count -gt 0 -and ($errorRows.errorText -join ' ') -match 'fictional extended diagnostic message') "$mode expanded source exposes the extended error delivered through the actual reader"
  Check-R6 (@($errorRows | Where-Object {-not $_.errorInsideLeftColumn -or $_.errorRight -gt $_.leftColumnRight+0.5 -or $_.copyRight -gt $_.leftColumnRight+0.5 -or $_.leftColumnRight -ge $_.statusLeft -or $_.errorTop -lt $_.rowTop-0.01 -or $_.errorBottom -gt $_.rowBottom+0.01 -or $_.copyTop -lt $_.rowTop-0.01 -or $_.copyBottom -gt $_.rowBottom+0.01}).Count -eq 0) "$mode expanded error and copy control remain fully inside the left column and source row before the short status"
  $errorScale=[double]$errorCard.glyphLayout.rasterizationScale
  Check-R6 (@($errorRows | Where-Object {$_.errorBottom-$_.errorTop -le 30 -or $_.errorActualWidth -le 0 -or $_.errorDesiredWidth -gt ([Math]::Ceiling($_.errorActualWidth*$errorScale)/$errorScale)+0.01 -or $_.copyRight -le $_.copyLeft -or $_.copyBottom -le $_.copyTop -or $_.copyTop -le $_.errorBottom -or [Math]::Abs($_.copyLeft-$_.errorLeft) -gt 0.01}).Count -eq 0) "$mode long reader error wraps to multiple bounded lines with the visible copy control below it and left-aligned"
  Check-R6 (@($errorCard.sourceLayouts | Where-Object {[Math]::Abs($_.statusRight-$errorCard.glyphLayout.glyphRight) -gt 0.5}).Count -eq 0) "$mode source status right edges align with the header battery glyph"
  Check-R6 (($errorCard.detailText -join ' ') -notmatch '73%|上次读数|Last available') "$mode source details avoid repeating retained numeric readings"
  Save-R6 ($mode+'-error-expanded') $expanded
  Capture-R6 'capture-main' ('r6-main-'+$mode+'-error.png')
  $null=Ui-R6 'collapse' '' $errorId
 }
 foreach($count in 1..3){
  $name=@('panel-one','panel-two','panel-three')[$count-1]
  $null=Scenario-R6 $name
  $small=OpenPanel-R6 $count
  $expectedClient=[Math]::Round((48*$count+78)*$small.status.panel.rasterizationScale)
  Check-R6 ([Math]::Abs($small.status.panel.clientHeight-$expectedClient) -le 1 -and $small.status.panel.scrollableHeight -le 0.5 -and $small.status.panel.fullyVisibleDeviceIds.Count -eq $count) "$count-device panel fits every 48 DIP row without overflow"
  Save-R6 ($name+'-size') $small
  $null=Ui-R6 'panel-close'
 }
 $null=Ui-R6 'system-theme' 'reset'
 $last=Command-R6 'status'
 Check-R6 ((Get-FileHash -LiteralPath $taskSettings).Hash -ceq $taskHash) 'r6 checks preserve the on-disk settings sentinel'
 $exit=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
 Check-R6 ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'r6 scenario exits normally'
 $events=@(Get-Content -LiteralPath $last.status.diagnosticLog | ForEach-Object {$_ | ConvertFrom-Json})
 $opened=@($events | Where-Object kind -EQ 'hid-open')
 $closed=@($events | Where-Object kind -EQ 'hid-closed' | ForEach-Object {$_.data.channelId})
 Check-R6 (@($opened | Where-Object {$_.data.channelId -notin $closed}).Count -eq 0 -and @($events | Where-Object {$_.kind -eq 'session-stopped' -and $_.data.physicallyDrained}).Count -gt 0) 'r6 checks release every fictional hardware channel'
 $taskResult.passed=$true
}catch{$taskResult.error=$_.Exception.Message;throw}
finally{
 if($null -ne $taskProcess -and -not $taskProcess.HasExited){try{$null=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe}catch{$taskResult.cleanupError=$_.Exception.Message}}
 if($null -ne $taskProcess){$taskProcess.Dispose()}
 $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json') -Encoding utf8
}
[pscustomobject]@{passed=$taskResult.passed;checks=$taskResult.checks.Count;evidenceDirectory=$taskDirectory}
