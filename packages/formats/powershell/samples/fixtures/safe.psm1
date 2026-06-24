Set-StrictMode -Version Latest

function Get-Greeting {
    param([string] $Name)
    Write-Output "Hello, $Name"
}

Get-Greeting -Name 'Cop'
