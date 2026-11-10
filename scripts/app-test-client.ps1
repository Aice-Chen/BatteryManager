[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]{1,64}$')][string]$PipeSuffix,
    [Parameter(Mandatory)][ValidateSet('status', 'refresh', 'quit', 'preferences', 'power', 'clock', 'scenario', 'ui')][string]$Command,
    [string]$Action,
    [string]$DeviceId,
    [string]$Value,
    [ValidateRange(1, 30000)][int]$ConnectTimeoutMilliseconds = 3000,
    [ValidateRange(1, 60000)][int]$ResponseTimeoutMilliseconds = 25000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$taskPipe = $null
$taskReader = $null
$taskDeadline = $null
try {
    $taskPipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.', "BatteryManager.Test.$PipeSuffix", [System.IO.Pipes.PipeDirection]::InOut,
        ([System.IO.Pipes.PipeOptions]::Asynchronous -bor [System.IO.Pipes.PipeOptions]::CurrentUserOnly))
    $null = $taskPipe.ConnectAsync($ConnectTimeoutMilliseconds).GetAwaiter().GetResult()
    $taskDeadline = [System.Threading.CancellationTokenSource]::new($ResponseTimeoutMilliseconds)
    $taskUtf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $taskPayload = @{ command = $Command }
    if ($Command -in @('preferences', 'power', 'clock', 'scenario', 'ui')) {
        if ([string]::IsNullOrEmpty($Action)) { throw 'This command requires -Action.' }
        $taskPayload.action = $Action
        if ($PSBoundParameters.ContainsKey('Value')) { $taskPayload.value = $Value }
        if (-not [string]::IsNullOrEmpty($DeviceId)) { $taskPayload.deviceId = $DeviceId }
    }
    $taskRequest = $taskPayload | ConvertTo-Json -Compress
    $taskBytes = $taskUtf8.GetBytes($taskRequest + "`n")
    $null = $taskPipe.WriteAsync($taskBytes, 0, $taskBytes.Length, $taskDeadline.Token).GetAwaiter().GetResult()
    $null = $taskPipe.FlushAsync($taskDeadline.Token).GetAwaiter().GetResult()
    $taskReader = [System.IO.StreamReader]::new($taskPipe, $taskUtf8, $false, 4096, $true)
    $taskLine = $taskReader.ReadLineAsync($taskDeadline.Token).AsTask().GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($taskLine)) { throw 'The app closed the test connection without a response.' }
    $taskResponse = $taskLine | ConvertFrom-Json -Depth 100
    if ($null -eq $taskResponse.PSObject.Properties['ok'] -or $taskResponse.ok -isnot [bool]) {
        throw 'The app returned a response without a Boolean ok field.'
    }
    if (-not $taskResponse.ok) {
        $taskErrorProperty = $taskResponse.PSObject.Properties['error']
        $taskDetail = if ($null -eq $taskErrorProperty) { 'Unknown app error.' } else { [string]$taskErrorProperty.Value }
        throw "The app rejected '$Command': $taskDetail"
    }
    if ($Command -ne 'quit' -and ($null -eq $taskResponse.PSObject.Properties['status'] -or $null -eq $taskResponse.status)) {
        throw "The '$Command' response did not contain a status snapshot."
    }
    Write-Output $taskResponse
}
catch {
    throw "App test command '$Command' failed on pipe '$PipeSuffix': $($_.Exception.Message)"
}
finally {
    if ($null -ne $taskReader) { $taskReader.Dispose() }
    if ($null -ne $taskPipe) { $taskPipe.Dispose() }
    if ($null -ne $taskDeadline) { $taskDeadline.Dispose() }
}
