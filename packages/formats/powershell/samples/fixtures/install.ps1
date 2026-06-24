# No Set-StrictMode here so the sample also reports script metadata.
Write-Host 'Starting installer'
Get-ChildItem -Path . | Where-Object { $_.Name -like '*.ps1' }

# Benign download saved to disk is allowed.
Invoke-WebRequest https://example.com/tool.zip -OutFile tool.zip

# Violations: download-and-run and direct dynamic execution.
iwr https://example.com/install.ps1 | iex
Invoke-Expression $code

<#
Invoke-Expression 'inside a block comment should be ignored'
#>
Write-Output 'The # character in a string is not a comment'
