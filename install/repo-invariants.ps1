<#
.SYNOPSIS
Checks repository invariants that are outside the Cop Codebase model.

.DESCRIPTION
This script is intended for local use and CI. It validates .csproj XML, the
solution file, and package manifests, then exits non-zero if any invariant is
violated.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$SolutionPath = Join-Path $RepoRoot 'cop.sln'
$script:ViolationCount = 0

function ConvertTo-RepoRelativePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $root = $RepoRoot
    if (-not $root.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $root += [System.IO.Path]::DirectorySeparatorChar
    }

    $rootUri = [System.Uri]::new($root)
    $pathUri = [System.Uri]::new($fullPath)
    return [System.Uri]::UnescapeDataString($rootUri.MakeRelativeUri($pathUri).ToString()).Replace('/', '\')
}

function Normalize-RepoPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return $Path.Replace('/', '\').TrimStart('.\').ToLowerInvariant()
}

function Write-CheckHeader {
    param([Parameter(Mandatory = $true)][string]$Name)

    Write-Host ""
    Write-Host "== $Name =="
}

function Write-CheckSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$Checked,
        [Parameter(Mandatory = $true)][int]$Violations,
        [string]$Detail = ''
    )

    $status = if ($Violations -eq 0) { 'PASS' } else { 'FAIL' }
    $suffix = if ([string]::IsNullOrWhiteSpace($Detail)) { '' } else { " - $Detail" }
    Write-Host "Summary: $status - $Checked checked, $Violations violation(s)$suffix"
    $script:ViolationCount += $Violations
}

function Get-ProjectReferences {
    param([Parameter(Mandatory = $true)][System.Xml.XmlDocument]$ProjectXml)

    return $ProjectXml.SelectNodes("//*[local-name()='ProjectReference']")
}

function Test-PrivateFalse {
    param([Parameter(Mandatory = $true)][System.Xml.XmlElement]$ProjectReference)

    $privateAttribute = $ProjectReference.GetAttribute('Private')
    if ($privateAttribute -and $privateAttribute.Trim().Equals('false', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    $privateElement = $ProjectReference.SelectSingleNode("./*[local-name()='Private']")
    if ($null -ne $privateElement -and $privateElement.InnerText.Trim().Equals('false', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $true
    }

    return $false
}

function Test-ProviderReferencesCorePrivateFalse {
    $checkName = 'provider-references-core-private-false'
    Write-CheckHeader $checkName

    $providerProjects = @(Get-ChildItem -Path (Join-Path $RepoRoot 'providers') -Recurse -Filter '*.csproj' -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })

    $checkedReferences = 0
    $violations = 0

    foreach ($project in $providerProjects) {
        [xml]$xml = Get-Content -LiteralPath $project.FullName -Raw
        foreach ($reference in (Get-ProjectReferences -ProjectXml $xml)) {
            $include = $reference.GetAttribute('Include')
            if ([string]::IsNullOrWhiteSpace($include)) {
                continue
            }

            $normalizedInclude = $include.Replace('/', '\')
            if (-not $normalizedInclude.EndsWith('\cop.csproj', [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $checkedReferences++
            if (-not (Test-PrivateFalse -ProjectReference $reference)) {
                $violations++
                $relativeProject = ConvertTo-RepoRelativePath $project.FullName
                Write-Host "VIOLATION: $relativeProject references $include but must set Private=false on that ProjectReference."
            }
        }
    }

    Write-CheckSummary $checkName $checkedReferences $violations 'provider references to cop.csproj'
}

function Get-SolutionProjectPaths {
    if (-not (Test-Path -LiteralPath $SolutionPath)) {
        throw "Solution file not found: $SolutionPath"
    }

    $paths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $projectPattern = 'Project\("[^"]+"\)\s*=\s*"[^"]+",\s*"([^"]+\.csproj)",'

    foreach ($line in Get-Content -LiteralPath $SolutionPath) {
        $match = [regex]::Match($line, $projectPattern)
        if ($match.Success) {
            [void]$paths.Add((Normalize-RepoPath $match.Groups[1].Value))
        }
    }

    return $paths
}

function Test-AllProjectsInSolution {
    $checkName = 'all-projects-in-solution'
    Write-CheckHeader $checkName

    $solutionProjects = Get-SolutionProjectPaths
    $repoProjects = @(Get-ChildItem -Path $RepoRoot -Recurse -Filter '*.csproj' -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.git)[\\/]' })

    $violations = 0

    foreach ($project in $repoProjects) {
        $relativePath = ConvertTo-RepoRelativePath $project.FullName
        if (-not $solutionProjects.Contains((Normalize-RepoPath $relativePath))) {
            $violations++
            Write-Host "VIOLATION: $relativePath is not referenced by cop.sln. Add it with: dotnet sln cop.sln add `"$relativePath`""
        }
    }

    Write-CheckSummary $checkName $repoProjects.Count $violations 'csproj files must be listed in cop.sln'
}

function Test-PackageUsesOwnProvider {
    param(
        [Parameter(Mandatory = $true)][string]$PackageDirectory,
        [Parameter(Mandatory = $true)][string]$PackageName
    )

    $srcDirectory = Join-Path $PackageDirectory 'src'
    if (-not (Test-Path -LiteralPath $srcDirectory)) {
        return $false
    }

    $escapedPackageName = [regex]::Escape($PackageName)
    $providerPattern = "provider\s*\(\s*'$escapedPackageName'"
    foreach ($sourceFile in Get-ChildItem -Path $srcDirectory -Recurse -Filter '*.cop' -File) {
        $nonCommentText = (Get-Content -LiteralPath $sourceFile.FullName |
            Where-Object { -not $_.TrimStart().StartsWith('#') }) -join "`n"
        if ($nonCommentText -match $providerPattern) {
            return $true
        }
    }

    return $false
}

function Get-JsonPropertyValue {
    param(
        [Parameter(Mandatory = $true)][object]$Json,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Json.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Test-ExternalProviderHasCopJson {
    $checkName = 'external-provider-has-cop-json'
    Write-CheckHeader $checkName

    # External CLR provider packages are package directories whose source calls
    # provider('<package name>') and whose sibling lib/ folder ships at least one
    # DLL. Built-in/non-provider packages can have manifests without CLR metadata.
    $libDirectories = Get-ChildItem -Path (Join-Path $RepoRoot 'packages') -Recurse -Directory -Filter 'lib'
    $checkedPackages = 0
    $violations = 0

    foreach ($libDirectoryInfo in $libDirectories) {
        $libDirectory = $libDirectoryInfo.FullName
        $dlls = @(Get-ChildItem -Path $libDirectory -Filter '*.dll' -File -ErrorAction SilentlyContinue)
        if ($dlls.Count -eq 0) {
            continue
        }

        $packageDirectory = $libDirectoryInfo.Parent.FullName
        $manifestPath = Join-Path $packageDirectory 'cop.json'
        $json = $null
        $packageName = $libDirectoryInfo.Parent.Name
        if (Test-Path -LiteralPath $manifestPath) {
            $json = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            $packageName = [string](Get-JsonPropertyValue $json 'name')
        }

        if ([string]::IsNullOrWhiteSpace($packageName)) {
            $violations++
            Write-Host "VIOLATION: $(ConvertTo-RepoRelativePath $manifestPath) has sibling lib/*.dll but no non-empty 'name' field."
            continue
        }

        if (-not (Test-PackageUsesOwnProvider -PackageDirectory $packageDirectory -PackageName $packageName)) {
            continue
        }

        $checkedPackages++
        $relativeManifest = ConvertTo-RepoRelativePath $manifestPath
        if ($null -eq $json) {
            $violations++
            Write-Host "VIOLATION: $(ConvertTo-RepoRelativePath $packageDirectory) uses provider('$packageName') and ships lib/*.dll but is missing cop.json."
            continue
        }

        $provider = [string](Get-JsonPropertyValue $json 'provider')
        $providerEntry = [string](Get-JsonPropertyValue $json 'providerEntry')
        $providerAssembly = [string](Get-JsonPropertyValue $json 'providerAssembly')

        if ($provider -ne 'clr') {
            $violations++
            Write-Host "VIOLATION: $relativeManifest uses provider('$packageName') and ships lib/*.dll but must set `"provider`": `"clr`"."
        }

        if ([string]::IsNullOrWhiteSpace($providerEntry)) {
            $violations++
            Write-Host "VIOLATION: $relativeManifest uses provider('$packageName') and ships lib/*.dll but must set a non-empty `"providerEntry`"."
        }

        if (-not [string]::IsNullOrWhiteSpace($providerAssembly)) {
            $assemblyPath = Join-Path $libDirectory $providerAssembly
            if (-not (Test-Path -LiteralPath $assemblyPath)) {
                $violations++
                Write-Host "VIOLATION: $relativeManifest declares providerAssembly '$providerAssembly' but $(ConvertTo-RepoRelativePath $assemblyPath) does not exist."
            }
        }
    }

    Write-CheckSummary $checkName $checkedPackages $violations 'external provider package manifests'
}

Test-ProviderReferencesCorePrivateFalse
Test-AllProjectsInSolution
Test-ExternalProviderHasCopJson

Write-Host ""
Write-Host "== Final summary =="
if ($script:ViolationCount -eq 0) {
    Write-Host "PASS - all repo invariants passed."
    exit 0
}

Write-Host "FAIL - $script:ViolationCount total violation(s)."
exit 1
