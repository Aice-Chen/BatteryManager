[CmdletBinding()]
param([Parameter(Mandatory)][string]$PreviewExe)

# Compatibility entry point: current battery-glyph checks supersede the old bar checks.
& (Join-Path $PSScriptRoot 'test-ui-r6.ps1') -PreviewExe $PreviewExe
