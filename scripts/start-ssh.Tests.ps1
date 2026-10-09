Describe 'Local OpenSSH startup' {
  BeforeEach {
    . (Join-Path $PSScriptRoot 'start-ssh.ps1')
    $script:fakeService = $null
    Mock Get-Service { $script:fakeService }
    Mock Get-LocalSshServerPath { 'C:\Program Files\OpenSSH\sshd.exe' }
    Mock New-Service { $script:fakeService = [pscustomobject]@{ Status = 'Stopped' } }
    Mock Start-Service { $script:fakeService = [pscustomobject]@{ Status = 'Running' } }
  }

  It 'Registers the installed binary when the service is missing' {
    Start-LocalSshServer | Should Be 'OpenSSH Server is running'
    Assert-MockCalled New-Service -Times 1 -Exactly -Scope It -ParameterFilter {
      $Name -eq 'sshd' -and $BinaryPathName -eq '"C:\Program Files\OpenSSH\sshd.exe"' -and $StartupType -eq 'Manual'
    }
    Assert-MockCalled Start-Service -Times 1 -Exactly -Scope It
  }

  It 'Starts an existing stopped service without replacing its configuration' {
    $script:fakeService = [pscustomobject]@{ Status = 'Stopped' }
    Start-LocalSshServer | Should Be 'OpenSSH Server is running'
    Assert-MockCalled New-Service -Times 0 -Exactly -Scope It
    Assert-MockCalled Start-Service -Times 1 -Exactly -Scope It
  }

  It 'Leaves an already running service alone' {
    $script:fakeService = [pscustomobject]@{ Status = 'Running' }
    Start-LocalSshServer | Should Be 'OpenSSH Server is running'
    Assert-MockCalled New-Service -Times 0 -Exactly -Scope It
    Assert-MockCalled Start-Service -Times 0 -Exactly -Scope It
  }
}

