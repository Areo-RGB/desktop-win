# Builds the standalone single-file Release FluentDesktop.exe.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#          [-Runtime win-x64|win-arm64] [-Output publish] [-Clean] [-Run] [-NoShortcut]
# Creates/updates a "Fluent Desktop" shortcut on the Desktop unless -NoShortcut.

param(
  [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-x64',
  [string]$Output = 'publish',
  [switch]$Clean,
  [switch]$Run,
  [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$OutDir = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $ProjectRoot $Output }

Push-Location $ProjectRoot
try {
  # A running copy from the output folder locks the exe and breaks the bundle step.
  $target = Join-Path $OutDir 'FluentDesktop.exe'
  Get-Process FluentDesktop -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $target } |
    ForEach-Object { Write-Output "closing running $target (pid $($_.Id))"; Stop-Process -Id $_.Id -Force; $_.WaitForExit(5000) | Out-Null }

  if ($Clean -and (Test-Path $OutDir)) { Remove-Item $OutDir -Recurse -Force }

  dotnet publish FluentDesktop\FluentDesktop.csproj -c Release -r $Runtime "-p:Platform=$Platform" `
    -p:SelfContained=true -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $OutDir
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

  $exe = Join-Path $OutDir 'FluentDesktop.exe'
  if (-not (Test-Path $exe)) { throw "missing $exe after publish" }
  $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
  Write-Output "Built $exe ($mb MB)"
  if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Fluent Desktop.lnk'
    $sc = (New-Object -ComObject WScript.Shell).CreateShortcut($lnk)
    $sc.TargetPath = $exe
    $sc.WorkingDirectory = $OutDir
    $sc.IconLocation = "$exe,0"
    $sc.Description = 'Fluent Desktop'
    $sc.Save()
    Write-Output "Shortcut: $lnk"
  }
  if ($Run) { Start-Process $exe }
}
finally { Pop-Location }
