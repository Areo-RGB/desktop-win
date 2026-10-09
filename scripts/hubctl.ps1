# Windows equivalent of scripts/hubctl.sh (which needs bash/pgrep/fuser).
# Controls servers/mcphub_fork via node directly.
#
# Usage: powershell -ExecutionPolicy Bypass -File scripts\hubctl.ps1 <action>
#   start-hub | stop-hub | start-tunnel | stop-tunnel | status | logs
#   start-all | stop-all
#
# Env: PORT (default 3000), DISABLE_WEB (true = headless, no dashboard UI),
#      TUNNEL_TOKEN_FILE (default ~\.cloudflared\mcp-hub.token).
# The hub reads servers/mcphub_fork/mcp_settings.json when its cwd is the hub
# dir (see src/utils/path.ts lookup order), so we always launch with cwd there.
# Dashboard auth is off via systemConfig.routing.skipAuth in that file, and
# MCP bearer auth is off via enableBearerAuth=false. Local trusted use only.

param([string]$Action)

$ErrorActionPreference = 'Stop'
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectRoot = Split-Path -Parent $ScriptDir
$HubDir = if ($env:MCPHUB_DIR) { $env:MCPHUB_DIR } `
  else { Join-Path $(if ($env:MCP_HUB_ROOT) { $env:MCP_HUB_ROOT } else { Join-Path $HOME 'Projects\mcp-hub' }) 'mcphub_fork' }
$Port = if ($env:PORT) { [int]$env:PORT } else { 3000 }
$LogFile = Join-Path ([System.IO.Path]::GetTempPath()) 'mcphub.log'
$PidFile = Join-Path ([System.IO.Path]::GetTempPath()) 'mcphub.pid'
$TraceFile = Join-Path ([System.IO.Path]::GetTempPath()) 'hubctl-trace.log'
$TunnelLog = Join-Path ([System.IO.Path]::GetTempPath()) 'cloudflared.log'
$TunnelPidFile = Join-Path ([System.IO.Path]::GetTempPath()) 'cloudflared-mcp.pid'
# Token for the remotely managed tunnel (ingress mcp.h03.uk -> localhost:3000).
$TunnelTokenFile = if ($env:TUNNEL_TOKEN_FILE) { $env:TUNNEL_TOKEN_FILE } `
  else { Join-Path $env:USERPROFILE '.cloudflared\mcp-hub.token' }

function Get-CloudflaredPath {
  $cmd = Get-Command cloudflared -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  foreach ($p in @("${env:ProgramFiles(x86)}\cloudflared\cloudflared.exe",
                   "$env:ProgramFiles\cloudflared\cloudflared.exe")) {
    if (Test-Path $p) { return $p }
  }
  throw 'cloudflared not found - run: winget install Cloudflare.cloudflared'
}

function Get-TunnelProcesses {
  # Match on our token file so other cloudflared instances (e.g. T3 Connect)
  # are never touched.
  $leaf = [regex]::Escape((Split-Path -Leaf $TunnelTokenFile))
  Get-CimInstance Win32_Process -Filter "Name='cloudflared.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match "--token-file\s+`"?[^`"]*$leaf" }
}

function Write-Trace([string]$msg) {
  try {
    $ts = (Get-Date).ToString('HH:mm:ss.fff')
    Add-Content $TraceFile "$ts [$PID] $msg" -ErrorAction SilentlyContinue
  } catch {}
}

function Test-PortOpen {
  try {
    $c = New-Object Net.Sockets.TcpClient
    $r = $c.BeginConnect('127.0.0.1', $Port, $null, $null)
    $ok = $r.AsyncWaitHandle.WaitOne(600)
    $c.Close()
    return $ok
  } catch { return $false }
}

function Get-HubProcesses {
  # PID file is authoritative; fall back to listeners on $Port, then to any
  # node running this hub's entrypoint (CommandLine has no cwd, so match it).
  Write-Trace 'Get-HubProcesses enter'
  $found = @()
  if (Test-Path $PidFile) {
    Write-Trace 'pidfile branch'
    try {
      $pid = [int](Get-Content $PidFile | Select-Object -First 1)
      # PIDs get reused: only trust the pidfile if it still points at the hub.
      $cmd = (Get-CimInstance Win32_Process -Filter "ProcessId=$pid" -ErrorAction SilentlyContinue).CommandLine
      if ($cmd -match 'dist.index\.js') {
        $p = Get-Process -Id $pid -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -eq 'node') { $found += $p }
      } else {
        Write-Trace "pidfile stale (pid $pid not the hub); ignoring"
      }
    } catch {}
  }
  try {
    Write-Trace 'netstat branch enter'
    $conns = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    Write-Trace 'netstat branch done'
    foreach ($c in $conns) {
      if ($c.OwningProcess -ne $PID) {
        $p = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
        if ($p) { $found += $p }
      }
    }
  } catch {}
  try {
    Write-Trace 'cim branch enter'
    Get-CimInstance Win32_Process -Filter "Name='node.exe'" -ErrorAction SilentlyContinue |
      Where-Object { $_.CommandLine -match 'dist.index\.js' } |
      ForEach-Object {
        $p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue
        if ($p) { $found += $p }
      }
    Write-Trace 'cim branch done'
  } catch {}
  Write-Trace 'Get-HubProcesses exit'
  $found | Sort-Object Id -Unique
}

switch ($Action) {
  'start-hub' {
    Write-Trace 'action start-hub enter'
    if (Test-PortOpen) { Write-Output "hub already listening on :$Port"; break }
    $entry = Join-Path $HubDir 'dist\index.js'
    if (-not (Test-Path $entry)) { throw "missing $entry - run: pnpm --dir <mcp-hub>\mcphub_fork build" }
    $env:PORT = "$Port"
    $ErrFile = "$LogFile.err"
    $proc = Start-Process -FilePath 'node' -ArgumentList 'dist\index.js' -WorkingDirectory $HubDir `
      -RedirectStandardOutput $LogFile -RedirectStandardError $ErrFile -WindowStyle Hidden -PassThru
    Set-Content $PidFile $proc.Id
    Write-Trace "action start-hub exit pid=$($proc.Id)"
    Write-Output "starting hub on :$Port (pid $($proc.Id), log: $LogFile)"
  }
  'stop-hub' {
    Write-Trace 'action stop-hub enter'
    $procs = Get-HubProcesses
    Write-Trace "stop-hub targets: $($procs.Id -join ',')"
    if ($procs) {
      $procs | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
      Write-Trace 'stop-hub kills issued'
      Start-Sleep -Milliseconds 800
      $left = Get-HubProcesses
      Write-Trace 'action stop-hub exit'
      if ($left) { Write-Output "stopped; still listening: $($left.Id -join ', ')" }
      else { Write-Output 'hub stopped' }
    }
    else { Write-Output 'hub not running' }
    Remove-Item $PidFile -ErrorAction SilentlyContinue
  }
  'start-tunnel' {
    if (Get-TunnelProcesses) { Write-Output 'tunnel already running'; break }
    if (-not (Test-Path $TunnelTokenFile)) { throw "missing tunnel token: $TunnelTokenFile" }
    $exe = Get-CloudflaredPath
    $proc = Start-Process -FilePath $exe `
      -ArgumentList 'tunnel', '--no-autoupdate', 'run', '--token-file', "`"$TunnelTokenFile`"" `
      -RedirectStandardOutput "$TunnelLog.out" -RedirectStandardError $TunnelLog -WindowStyle Hidden -PassThru
    Set-Content $TunnelPidFile $proc.Id
    Write-Output "starting cloudflared (pid $($proc.Id), log: $TunnelLog)"
  }
  'stop-tunnel' {
    $procs = Get-TunnelProcesses
    if ($procs) {
      $procs | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
      Write-Output 'tunnel stopped'
    }
    else { Write-Output 'tunnel not running' }
    Remove-Item $TunnelPidFile -ErrorAction SilentlyContinue
  }
  'start-all' {
    # no ssh daemon managed on Windows; hub + tunnel only
    & $MyInvocation.MyCommand.Path 'start-hub'
    & $MyInvocation.MyCommand.Path 'start-tunnel'
  }
  'stop-all' {
    & $MyInvocation.MyCommand.Path 'stop-hub'
    & $MyInvocation.MyCommand.Path 'stop-tunnel'
  }
  'start-ssh' {
    & (Join-Path $ScriptDir 'start-ssh.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
  }
  'status' {
    if (Test-PortOpen) { Write-Output "hub UP on :$Port" }
    else { Write-Output 'hub DOWN'; exit 1 }
  }
  'logs' {
    if (Test-Path $LogFile) { Get-Content $LogFile -Tail 50 } else { Write-Output "no log at $LogFile" }
  }
  default {
    Write-Error "usage: hubctl.ps1 {start-hub|stop-hub|start-tunnel|stop-tunnel|start-all|stop-all|status|logs}"
    exit 2
  }
}
