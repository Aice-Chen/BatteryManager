# Stable-release entry point; share the clean build and verification path with previews.
[CmdletBinding()]
param([string]$OutputDirectory = '.artifacts/packages')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'package-preview.ps1') -OutputDirectory $OutputDirectory -Release
