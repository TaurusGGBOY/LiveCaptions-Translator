param(
  [Parameter(Mandatory=$true)][string]$OriginalExe,
  [Parameter(Mandatory=$true)][string]$DependencyDirectory,
  [Parameter(Mandatory=$true)][string]$ILRepack,
  [string]$Dotnet = 'dotnet',
  [string]$Node = 'node',
  [string]$CecilPath = '',
  [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts')
)
$ErrorActionPreference = 'Stop'
function Check-Exit { if ($LASTEXITCODE -ne 0) { throw "Build command failed: $LASTEXITCODE" } }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
& $Dotnet build (Join-Path $PSScriptRoot 'NotionExtension.csproj') -c Release --nologo
Check-Exit
$cecilArgs = @()
if ($CecilPath) { $cecilArgs += "-p:CecilPath=$CecilPath" }
& $Dotnet build (Join-Path $PSScriptRoot 'Patcher/Patcher.csproj') -c Release --nologo @cecilArgs
Check-Exit
$originalDll = Join-Path $OutputDirectory 'original.dll'
$mergedDll = Join-Path $OutputDirectory 'merged.dll'
$patchedDll = Join-Path $OutputDirectory 'LiveCaptionsTranslator.dll'
& $Node (Join-Path $PSScriptRoot 'bundle.cjs') extract $OriginalExe $originalDll
Check-Exit
& $ILRepack "/out:$mergedDll" "/lib:$DependencyDirectory" $originalDll (Join-Path $PSScriptRoot 'bin/Release/net8.0-windows/NotionExtension.dll')
Check-Exit
& $Dotnet (Join-Path $PSScriptRoot 'Patcher/bin/Release/net8.0/Patcher.dll') $mergedDll $patchedDll $originalDll
Check-Exit
& $Node (Join-Path $PSScriptRoot 'bundle.cjs') replace $OriginalExe $patchedDll (Join-Path $OutputDirectory 'LiveCaptionsTranslator.exe')
Check-Exit
Write-Output "Candidate built. Installation and configuration were not modified."
