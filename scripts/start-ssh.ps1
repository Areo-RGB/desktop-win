param([switch]$Elevated, [string]$ResultPath)

function Get-LocalSshServerPath {
  $command = Get-Command sshd.exe -ErrorAction SilentlyContinue
  $candidates = @(
    $(if ($command) { $command.Source }),
    (Join-Path $env:ProgramFiles 'OpenSSH\sshd.exe'),
    (Join-Path $env:WINDIR 'System32\OpenSSH\sshd.exe')
  )
  foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
  }
  throw 'Install OpenSSH Server in Windows Optional Features, then click Start SSH again.'
}

function Start-LocalSshServer {
  $service = Get-Service -Name sshd -ErrorAction SilentlyContinue
  if (-not $service) {
    $server = Get-LocalSshServerPath
    # Manual startup preserves the user's choice to start SSH from the app.
    New-Service -Name sshd -BinaryPathName ('"' + $server + '"') `
      -DisplayName 'OpenSSH SSH Server' -StartupType Manual -ErrorAction Stop | Out-Null
    $service = Get-Service -Name sshd -ErrorAction Stop
  }
  if ($service.Status -ne 'Running') { Start-Service -Name sshd -ErrorAction Stop }
  if ((Get-Service -Name sshd -ErrorAction Stop).Status -ne 'Running') {
    throw 'OpenSSH Server did not reach the running state. Check its service configuration.'
  }
  return 'OpenSSH Server is running'
}

function Initialize-LocalSshKey {
  $settingsDir = if ($env:FLUENT_DESKTOP_CONFIG_DIR) { $env:FLUENT_DESKTOP_CONFIG_DIR } `
    else { Join-Path $env:APPDATA 'fluent_desktop' }
  $settingsFile = Join-Path $settingsDir 'ssh_mcp.json'
  if (-not (Test-Path -LiteralPath $settingsFile)) { return }
  $settings = Get-Content -LiteralPath $settingsFile -Raw | ConvertFrom-Json
  # Authorize only the signed-in user's configured local key.
  if (-not $settings.keyPath -or $settings.host -notin @('127.0.0.1', 'localhost', '::1') `
      -or $settings.username -ne $env:USERNAME) { return }
  if (-not (Test-Path -LiteralPath $settings.keyPath)) { throw 'The configured SSH private key was not found.' }
  $keygen = Join-Path (Split-Path -Parent (Get-LocalSshServerPath)) 'ssh-keygen.exe'
  $publicKey = & $keygen -y -P '""' -f $settings.keyPath 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $publicKey) {
    throw 'Could not read the public key. Use an unencrypted SSH key or configure password authentication.'
  }
  $keysPath = Join-Path $env:ProgramData 'ssh\administrators_authorized_keys'
  $entries = if (Test-Path -LiteralPath $keysPath) { @(Get-Content -LiteralPath $keysPath) } else { @() }
  $keyParts = ($publicKey.Trim() -split '\s+')
  $exists = @($entries | Where-Object {
    $parts = $_.Trim() -split '\s+'
    $parts.Count -ge 2 -and $parts[0] -eq $keyParts[0] -and $parts[1] -eq $keyParts[1]
  }).Count -gt 0
  if (-not $exists) { $entries += $publicKey.Trim() }
  # OpenSSH requires this administrators key file to be writable only by Administrators and SYSTEM.
  $acl = New-Object Security.AccessControl.FileSecurity
  $admins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
  $acl.SetOwner($admins)
  $acl.SetAccessRuleProtection($true, $false)
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', 'Allow')))
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($system, 'FullControl', 'Allow')))
  if (-not $exists) { $entries | Set-Content -LiteralPath $keysPath -Encoding ascii }
  Set-Acl -LiteralPath $keysPath -AclObject $acl
}

# Dot sourcing exposes the functions for tests without starting services or UAC.
if ($MyInvocation.InvocationName -eq '.') { return }
$ErrorActionPreference = 'Stop'

try {
  $service = Get-Service -Name sshd -ErrorAction SilentlyContinue
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = New-Object Security.Principal.WindowsPrincipal($identity)
  $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($isAdmin) {
    $message = Start-LocalSshServer
    Initialize-LocalSshKey
  } else {
    if ($Elevated) { throw 'Administrator access is required to register or start OpenSSH Server.' }
    if (-not $service) { Get-LocalSshServerPath | Out-Null }
    $resultDir = Join-Path $env:LOCALAPPDATA 'fluent_desktop'
    New-Item -ItemType Directory -Path $resultDir -Force | Out-Null
    $resultFile = Join-Path $resultDir ('ssh-start-' + [guid]::NewGuid().ToString('N') + '.json')
    try {
      $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Elevated -ResultPath "' + $resultFile + '"'
      try {
        $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') `
          -Verb RunAs -ArgumentList $arguments -WindowStyle Hidden -PassThru
      } catch {
        throw 'Windows administrator approval is required to start SSH. Accept the UAC prompt and try again.'
      }
      if (-not $process.WaitForExit(45000)) {
        throw 'SSH setup is still waiting for Windows administrator approval. Accept the UAC prompt and check the SSH status.'
      }
      if (-not (Test-Path -LiteralPath $resultFile)) { throw 'SSH setup ended without a result. Try Start SSH again.' }
      $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
      if (-not $result.ok) { throw $result.message }
      $message = $result.message
    } finally {
      Remove-Item -LiteralPath $resultFile -Force -ErrorAction SilentlyContinue
    }
  }
  if ($ResultPath) { @{ ok = $true; message = $message } | ConvertTo-Json | Set-Content -LiteralPath $ResultPath }
  Write-Output $message
  exit 0
} catch {
  $message = $_.Exception.Message
  if ($ResultPath) { @{ ok = $false; message = $message } | ConvertTo-Json | Set-Content -LiteralPath $ResultPath }
  Write-Output $message
  exit 1
}
