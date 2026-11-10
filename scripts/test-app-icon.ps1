[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\', '/')
. (Join-Path $PSScriptRoot 'portable-common.ps1')
$taskExe = Resolve-PortableProjectPath $PreviewExe $taskRoot -ArtifactsOnly
if ($taskExe -eq (Join-Path $taskRoot '.artifacts/preview/BatteryManager.App.exe')) { throw 'Use an isolated publish directory.' }
if (@(Get-Process -Name BatteryManager.App -ErrorAction SilentlyContinue).Count) { throw 'Exit the existing app before this test.' }
$taskSuffix = 'app-icon-' + [Guid]::NewGuid().ToString('N')
$taskDirectory = Join-Path $taskRoot ('.artifacts/app-tests/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $taskSuffix)
$null = New-Item -ItemType Directory -Path $taskDirectory
$taskSettings = Join-Path (Split-Path $taskExe) 'settings.json'
if (-not (Test-Path -LiteralPath $taskSettings)) { [IO.File]::WriteAllText($taskSettings, '{"isolated-app-icon-test":true}') }
$taskSettingsHash = (Get-FileHash -LiteralPath $taskSettings).Hash
$taskClient = Join-Path $PSScriptRoot 'app-test-client.ps1'
$taskProcess = $null
$taskResult = [ordered]@{
    passed = $false; hardwareMode = $false; checks = @(); evidenceDirectory = $taskDirectory
    resourceFrames = @(); extractedFrames = @(); nativeCaptureCount = 0; captures = @()
    pixelAlignmentVerified = $false; taskbarOrAltTabVerified = $false; crossMonitorDpiVerified = $false
    limitations = @('Native window PNGs require actual pixel inspection; export success is not alignment proof.', 'Executable resources and explicit window icons do not establish which frame the shell taskbar or Alt+Tab actually displays.', 'No physical input, real hardware, system setting, or cross-monitor DPI change is performed.')
}
Add-Type -AssemblyName System.Drawing
if (-not ('AppIconCheck.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
namespace AppIconCheck {
 public static class Native {
  [StructLayout(LayoutKind.Sequential)] public struct IconInfo { public int IsIcon; public uint XHotspot,YHotspot; public IntPtr Mask,Color; }
  [StructLayout(LayoutKind.Sequential)] public struct BitmapInfo { public int Type,Width,Height,WidthBytes; public ushort Planes,BitsPixel; public IntPtr Bits; }
  public sealed class Dimensions { public int Width,Height; public long Handle; }
  public sealed class ResourceFrame { public int Width,Height,ResourceId,Bytes; public bool Identical; }
  public sealed class ResourceReport { public string[] Groups = Array.Empty<string>(); public int GroupId; public ResourceFrame[] Frames = Array.Empty<ResourceFrame>(); }
  private delegate bool EnumName(IntPtr module,IntPtr type,IntPtr name,IntPtr data);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr LoadLibraryExW(string path,IntPtr file,uint flags);
  [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern bool EnumResourceNamesW(IntPtr module,IntPtr type,EnumName callback,IntPtr data);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr FindResourceW(IntPtr module,IntPtr name,IntPtr type);
  [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module,IntPtr resource);
  [DllImport("kernel32.dll")] private static extern IntPtr LoadResource(IntPtr module,IntPtr resource);
  [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern uint PrivateExtractIconsW(string file,int index,int cx,int cy,[Out]IntPtr[] icons,[Out]uint[] ids,uint count,uint flags);
  [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
  [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon,out IconInfo info);
  [DllImport("gdi32.dll",EntryPoint="GetObjectW")] private static extern int GetObject(IntPtr obj,int size,out BitmapInfo info);
  [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern int GetSystemMetricsForDpi(int index,uint dpi);
  [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr SendMessageTimeoutW(IntPtr hwnd,uint msg,UIntPtr wp,IntPtr lp,uint flags,uint timeout,out UIntPtr result);
  private static byte[] Resource(IntPtr module,int name,int type) {
   var r=FindResourceW(module,(IntPtr)name,(IntPtr)type); if(r==IntPtr.Zero) throw new InvalidDataException("Resource not found: "+type+"/"+name);
   var n=checked((int)SizeofResource(module,r)); var p=LockResource(LoadResource(module,r));
   if(n==0 || p==IntPtr.Zero) throw new InvalidDataException("Empty resource.");
   var bytes=new byte[n]; Marshal.Copy(p,bytes,0,n); return bytes;
  }
  public static ResourceReport CompareResources(string exe,string ico) {
   var module=LoadLibraryExW(exe,IntPtr.Zero,2); if(module==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
   try {
    var names=new List<string>(); EnumName callback=(m,t,n,d)=>{ names.Add(((ulong)n.ToInt64()>>16)==0?n.ToInt64().ToString():Marshal.PtrToStringUni(n)); return true; };
    EnumResourceNamesW(module,(IntPtr)14,callback,IntPtr.Zero); GC.KeepAlive(callback);
    var group=Resource(module,32512,14); var source=File.ReadAllBytes(ico);
    if(BitConverter.ToUInt16(group,0)!=0 || BitConverter.ToUInt16(group,2)!=1 || BitConverter.ToUInt16(source,0)!=0 || BitConverter.ToUInt16(source,2)!=1) throw new InvalidDataException("Invalid icon header.");
    int count=BitConverter.ToUInt16(group,4); if(count!=BitConverter.ToUInt16(source,4)) throw new InvalidDataException("Icon frame count differs.");
    var result=new ResourceReport {Groups=names.ToArray(),GroupId=32512,Frames=new ResourceFrame[count]};
    for(int i=0;i<count;i++) {
     int a=6+14*i,b=6+16*i; int id=BitConverter.ToUInt16(group,a+12); var payload=Resource(module,id,3);
     int bytes=checked((int)BitConverter.ToUInt32(source,b+8)); int offset=checked((int)BitConverter.ToUInt32(source,b+12));
     bool same=payload.Length==bytes; for(int k=0;k<12;k++) same &= group[a+k]==source[b+k];
     for(int k=0;same && k<bytes;k++) same &= payload[k]==source[offset+k];
     result.Frames[i]=new ResourceFrame {Width=source[b]==0?256:source[b],Height=source[b+1]==0?256:source[b+1],ResourceId=id,Bytes=bytes,Identical=same};
    }
    return result;
   } finally {FreeLibrary(module);}
  }
  public static IntPtr Extract(string file,int size) {
   var icons=new IntPtr[1]; var ids=new uint[1]; var n=PrivateExtractIconsW(file,0,size,size,icons,ids,1,0);
   if(n!=1 || icons[0]==IntPtr.Zero) { if(icons[0]!=IntPtr.Zero) DestroyIcon(icons[0]); throw new InvalidDataException("Icon extraction failed at "+size); }
   return icons[0];
  }
  public static Dimensions Size(IntPtr icon) {
   IconInfo info; if(icon==IntPtr.Zero || !GetIconInfo(icon,out info)) throw new InvalidDataException("GetIconInfo failed.");
   try {
    BitmapInfo bitmap; var handle=info.Color!=IntPtr.Zero?info.Color:info.Mask;
    if(GetObject(handle,Marshal.SizeOf<BitmapInfo>(),out bitmap)==0) throw new InvalidDataException("GetObject failed.");
    return new Dimensions {Width=bitmap.Width,Height=info.Color!=IntPtr.Zero?bitmap.Height:bitmap.Height/2,Handle=icon.ToInt64()};
   } finally {if(info.Color!=IntPtr.Zero) DeleteObject(info.Color);if(info.Mask!=IntPtr.Zero) DeleteObject(info.Mask);}
  }
  public static IntPtr WindowIcon(IntPtr hwnd,bool big) {
   UIntPtr icon; if(SendMessageTimeoutW(hwnd,0x007f,(UIntPtr)(big?1u:0u),IntPtr.Zero,2,2000,out icon)==IntPtr.Zero) throw new TimeoutException("WM_GETICON did not complete.");
   return (IntPtr)unchecked((long)icon.ToUInt64());
  }
 }
}
'@
}
function Check-Icon([bool]$Condition, [string]$Name) { if (-not $Condition) { throw $Name }; $taskResult.checks += $Name }
function Save-Icon([string]$Name, $Value) { $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory ($Name + '.json')) -Encoding utf8 }
function Command-Icon([string]$Command, [string]$Action = '', [string]$Value = '') {
    $arguments = @{ PipeSuffix=$taskSuffix;Command=$Command;ConnectTimeoutMilliseconds=500;ResponseTimeoutMilliseconds=20000 }
    if($Action){$arguments.Action=$Action};if($Value){$arguments.Value=$Value}; & $taskClient @arguments
}
function Ui-Icon([string]$Action, [string]$Value = '') { Command-Icon 'ui' $Action $Value }
function Wait-Icon([scriptblock]$Condition = {param($s) $true}) {
    $watch=[Diagnostics.Stopwatch]::StartNew();$last=$null
    while($watch.Elapsed.TotalSeconds -lt 20){
        if($taskProcess.HasExited){throw 'Icon scenario exited unexpectedly.'}
        try{$last=Command-Icon 'status';$s=$last.status
            if(-not $s.scenario.applying -and -not $s.session.isRefreshing -and $s.session.outstandingOperations -eq 0 -and $s.view.version -eq $s.session.state.version -and (& $Condition $s)){return $last}
        }catch{if($watch.Elapsed.TotalSeconds -ge 20){throw}}
        Start-Sleep -Milliseconds 50
    }
    if($null -ne $last){Save-Icon 'timeout-state' $last};throw 'Icon scenario did not settle.'
}
function Export-IconBitmap([IntPtr]$Handle, [string]$Name) {
    $icon=[Drawing.Icon]::FromHandle($Handle)
    try{$bitmap=$icon.ToBitmap();try{$bitmap.Save((Join-Path $taskDirectory $Name),[Drawing.Imaging.ImageFormat]::Png)}finally{$bitmap.Dispose()}}finally{$icon.Dispose()}
}
function Capture-Icon([string]$Name, [string]$Surface) {
    Start-Sleep -Milliseconds 500
    $before=Command-Icon 'status'
    if($Surface -eq 'panel' -and (-not $before.status.panel.visible -or $before.status.panel.contentOpacity -lt 0.999)){Save-Icon ($Name+'-interrupted') $before;throw 'Panel closed before capture.'}
    $action=if($Surface -eq 'panel'){'capture-panel-native'}else{'capture-main-native'}
    $file='r12-'+$Name+'-native.png';$reply=Ui-Icon $action $file
    $path=Join-Path (Join-Path (Split-Path $reply.status.diagnosticLog) 'screenshots') $file
    $metadata=[IO.Path]::ChangeExtension($path,'.json')
    Check-Icon ((Test-Path -LiteralPath $path) -and (Test-Path -LiteralPath $metadata)) "$Name native window image and metadata exist"
    Save-Icon $Name $reply
    $taskResult.nativeCaptureCount++;$taskResult.captures += [pscustomobject]@{name=$Name;image=$path;metadata=$metadata;state=(Join-Path $taskDirectory ($Name+'.json'));action=$action}
}
try {
    $sourceIcon=Join-Path $taskRoot 'src/BatteryManager.App/Assets/AppIcon.ico'
    $resources=[AppIconCheck.Native]::CompareResources($taskExe,$sourceIcon);Save-Icon 'exe-resources' $resources
    Check-Icon ($resources.Frames.Count -gt 0 -and @($resources.Frames | Where-Object {-not $_.Identical}).Count -eq 0) 'Every embedded executable icon frame is byte-identical to its source ICO frame'
    $taskResult.resourceFrames=$resources.Frames
    foreach($size in @(16,20,24,30,32,40,48,256)){
        $actual=[IntPtr]::Zero;$reference=[IntPtr]::Zero
        try{
            $actual=[AppIconCheck.Native]::Extract($taskExe,$size);$reference=[AppIconCheck.Native]::Extract($sourceIcon,$size)
            $dimensions=[AppIconCheck.Native]::Size($actual)
            Check-Icon ($dimensions.Width -eq $size -and $dimensions.Height -eq $size) "Windows extracts a ${size}x${size} executable icon"
            Export-IconBitmap $actual ('exe-'+$size+'.png');Export-IconBitmap $reference ('source-ico-'+$size+'.png')
            # Both bitmaps pass through the same Windows extraction and managed HICON conversion.
            # Their equality establishes executable/source resource rendering, not Shell frame selection.
            $same=(Get-FileHash (Join-Path $taskDirectory ('exe-'+$size+'.png'))).Hash -ceq (Get-FileHash (Join-Path $taskDirectory ('source-ico-'+$size+'.png'))).Hash
            Check-Icon $same "Windows-rendered executable and source ICO match at $size pixels"
            $taskResult.extractedFrames += [pscustomobject]@{size=$size;image=('exe-'+$size+'.png');reference=('source-ico-'+$size+'.png');identical=$same}
        }finally{if($actual -ne [IntPtr]::Zero){$null=[AppIconCheck.Native]::DestroyIcon($actual)};if($reference -ne [IntPtr]::Zero){$null=[AppIconCheck.Native]::DestroyIcon($reference)}}
    }
    $taskProcess=Start-Process -FilePath $taskExe -ArgumentList @('--scenario','connection-switch','--test-pipe',$taskSuffix) -WorkingDirectory (Split-Path $taskExe) -WindowStyle Hidden -PassThru
    $ready=Wait-Icon {param($s) $s.scenario.name -eq 'connection-switch' -and $s.view.cards.Count -eq 3}
    Check-Icon (-not $ready.status.scenario.usesRealHardware -and -not $ready.status.scenario.savesPreferences -and $ready.status.view.modeBanner -match '演示数据') 'Icon checks use labelled fictional I/O and memory-only settings'
    Save-Icon 'startup' $ready
    $taskProcess.Refresh();$window=$taskProcess.MainWindowHandle
    $dpi=[AppIconCheck.Native]::GetDpiForWindow($window)
    Check-Icon ($window -ne [IntPtr]::Zero -and $dpi -gt 0) 'The scenario main window exposes an actual window DPI'
    $icons=@()
    foreach($big in @($false,$true)){
        $kind=if($big){'big'}else{'small'};$metric=if($big){11}else{49}
        $handle=[AppIconCheck.Native]::WindowIcon($window,$big)
        Check-Icon ($handle -ne [IntPtr]::Zero) "WM_GETICON returns a nonzero $kind icon"
        $dimensions=[AppIconCheck.Native]::Size($handle);$expected=[AppIconCheck.Native]::GetSystemMetricsForDpi($metric,$dpi)
        Check-Icon ($dimensions.Width -eq $expected -and $dimensions.Height -eq $expected) "$kind window icon uses the system's $dpi DPI dimensions"
        # WM_GETICON is borrowed ownership: never DestroyIcon on these handles.
        Export-IconBitmap $handle ('window-'+$kind+'.png')
        $icons += [pscustomobject]@{kind=$kind;dpi=$dpi;expected=$expected;dimensions=$dimensions}
    }
    Save-Icon 'window-icons' $icons;$taskResult.windowIcons=$icons
    foreach($theme in @('dark','light')){
        $null=Ui-Icon 'window-show';$null=Ui-Icon 'system-theme' ($theme+','+$theme)
        $null=Wait-Icon {param($s) $s.theme.simulated -and $s.view.mainTheme -eq $theme -and $s.panel.theme -eq $theme}
        Capture-Icon ($theme+'-main') 'main'
        if($theme -eq 'dark'){
            $null=Ui-Icon 'window-hide';$null=Wait-Icon {param($s) -not [AppIconCheck.Native]::IsWindowVisible($window)}
            Check-Icon (-not [AppIconCheck.Native]::IsWindowVisible($window)) 'The main window hides through the formal application path'
            $null=Ui-Icon 'window-show';$null=Wait-Icon {param($s) [AppIconCheck.Native]::IsWindowVisible($window)}
            Check-Icon ([AppIconCheck.Native]::IsWindowVisible($window)) 'The main window restores through the formal application path'
            Capture-Icon 'dark-main-restored' 'main'
        }
        $null=Ui-Icon 'panel-open'
        $null=Wait-Icon {param($s) $s.panel.visible -and $s.panel.contentOpacity -ge 0.999 -and [Math]::Abs($s.panel.contentOffsetY) -lt 0.0001 -and $s.panel.cards.Count -eq 3}
        Capture-Icon ($theme+'-panel') 'panel'
        $null=Ui-Icon 'panel-close';$null=Wait-Icon {param($s) -not $s.panel.visible}
    }
    $null=Ui-Icon 'system-theme' 'reset';$last=Command-Icon 'status'
    Check-Icon ($taskResult.nativeCaptureCount -eq 5) 'Both themes and a hidden/restored main window provide five native images'
    Check-Icon ((Get-FileHash -LiteralPath $taskSettings).Hash -ceq $taskSettingsHash) 'Icon checks preserve the isolated on-disk settings sentinel'
    $exit=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe
    Check-Icon ($exit.outcome -eq 'Exited' -and $taskProcess.WaitForExit(1000) -and $taskProcess.ExitCode -eq 0) 'Icon scenario exits normally'
    $events=@(Get-Content -LiteralPath $last.status.diagnosticLog | ForEach-Object {$_ | ConvertFrom-Json})
    $opened=@($events | Where-Object kind -EQ 'hid-open');$closed=@($events | Where-Object kind -EQ 'hid-closed' | ForEach-Object {$_.data.channelId})
    Check-Icon ($opened.Count -gt 0 -and @($opened | Where-Object {$_.data.channelId -notin $closed}).Count -eq 0 -and @($events | Where-Object {$_.kind -eq 'session-stopped' -and $_.data.physicallyDrained}).Count -gt 0) 'Icon scenario drains every recorded fictional HID channel'
    $taskResult.passed=$true
}catch{
    $taskResult.error=$_.Exception.Message
    if($null -ne $taskProcess -and -not $taskProcess.HasExited){try{Save-Icon 'failed-state' (Command-Icon 'status')}catch{}}
    throw
}finally{
    if($null -ne $taskProcess -and -not $taskProcess.HasExited){try{$null=& (Join-Path $PSScriptRoot 'stop-preview.ps1') -PreviewExe $taskExe}catch{$taskResult.cleanupError=$_.Exception.Message}}
    if($null -ne $taskProcess){$taskProcess.Dispose()}
    $taskResult | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath (Join-Path $taskDirectory 'result.json') -Encoding utf8
}
[pscustomobject]@{passed=$taskResult.passed;checks=$taskResult.checks.Count;nativeCaptureCount=$taskResult.nativeCaptureCount;evidenceDirectory=$taskDirectory;pixelAlignmentVerified=$false;taskbarOrAltTabVerified=$false}
