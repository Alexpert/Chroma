<#
.SYNOPSIS
  Builds the VS Code extension into a .vsix.

.DESCRIPTION
  editors/vscode is a manifest, a TextMate grammar and one file of plain JavaScript: nothing is
  compiled, bundled or downloaded, and no dependency is declared. A .vsix is a zip in the OPC
  layout, so this writes the two files that layout needs and zips the folder, rather than asking
  for Node and vsce to do the same thing.

  The version comes from Directory.Build.props, which is where the whole project's version lives.

.PARAMETER Version
  Overrides the version, which is otherwise read from Directory.Build.props.

.PARAMETER Output
  Where the .vsix is written. Defaults to dist/, which is not committed.

.PARAMETER Install
  Installs the result into VS Code afterwards, which needs `code` on PATH.

.EXAMPLE
  powershell -File tools/pack-vscode.ps1
  powershell -File tools/pack-vscode.ps1 -Install
#>

[CmdletBinding()]
param(
    [string] $Version,
    [string] $Output = 'dist',
    [switch] $Install
)

# As in publish-release.ps1: under 'Stop', a line `code` writes to stderr becomes a terminating
# error. Exit codes are checked where they happen instead.
$ErrorActionPreference = 'Continue'

$repository = Split-Path -Parent $PSScriptRoot
$source     = Join-Path $repository 'editors/vscode'

if (-not $Version) {
    $properties = Join-Path $repository 'Directory.Build.props'
    $match = Select-String -Path $properties -Pattern '<Version>(.+?)</Version>'

    if (-not $match) {
        throw "no <Version> in $properties, and none given with -Version"
    }

    $Version = $match.Matches[0].Groups[1].Value
}

# --- the two files the .vsix layout needs, and the extension itself --------------------------

# Publisher and identity are what VS Code names the extension by: alexpert.chroma. This is not
# published to the marketplace -- the .vsix is attached to the GitHub release -- but the
# identity still has to be stable, or an update installs beside the old one instead of over it.
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Language="en-US" Id="chroma" Version="$Version" Publisher="alexpert" />
    <DisplayName>Chroma scene files</DisplayName>
    <Description xml:space="preserve">Syntax highlighting and scene diagnostics for the .chroma scene language.</Description>
    <Tags>chroma,csg,ray tracing,scene</Tags>
    <Categories>Programming Languages,Linters</Categories>
    <GalleryFlags>Public</GalleryFlags>
    <Properties>
      <Property Id="Microsoft.VisualStudio.Code.Engine" Value="^1.75.0" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionDependencies" Value="" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionPack" Value="" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionKind" Value="workspace" />
      <Property Id="Microsoft.VisualStudio.Services.Links.Source" Value="https://github.com/Alexpert/Chroma" />
    </Properties>
    <License>extension/LICENSE.txt</License>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Code" />
  </Installation>
  <Dependencies />
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
    <Asset Type="Microsoft.VisualStudio.Services.Content.Details" Path="extension/README.md" Addressable="true" />
    <Asset Type="Microsoft.VisualStudio.Services.Content.License" Path="extension/LICENSE.txt" Addressable="true" />
  </Assets>
</PackageManifest>
"@

# Every extension present in the package needs a type here, LICENSE included, which is why it is
# renamed LICENSE.txt on the way in.
$contentTypes = @'
<?xml version="1.0" encoding="utf-8"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension=".json" ContentType="application/json" />
  <Default Extension=".js" ContentType="application/javascript" />
  <Default Extension=".md" ContentType="text/markdown" />
  <Default Extension=".txt" ContentType="text/plain" />
  <Default Extension=".vsixmanifest" ContentType="text/xml" />
</Types>
'@

# Source path -> where it goes inside the package.
$files = [ordered]@{
    'package.json'                   = 'extension/package.json'
    'language-configuration.json'    = 'extension/language-configuration.json'
    'extension.js'                   = 'extension/extension.js'
    'README.md'                      = 'extension/README.md'
    'syntaxes/chroma.tmLanguage.json' = 'extension/syntaxes/chroma.tmLanguage.json'
}

# --- staging ---------------------------------------------------------------------------------

$staging = Join-Path ([System.IO.Path]::GetTempPath()) "chroma-vsix-$([guid]::NewGuid())"
$utf8    = New-Object System.Text.UTF8Encoding $false

try {
    New-Item -ItemType Directory -Path $staging | Out-Null

    foreach ($relative in $files.Keys) {
        $from = Join-Path $source $relative

        if (-not (Test-Path $from)) {
            throw "editors/vscode is missing $relative"
        }

        $to = Join-Path $staging $files[$relative]
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
        Copy-Item $from $to
    }

    Copy-Item (Join-Path $repository 'LICENSE') (Join-Path $staging 'extension/LICENSE.txt')

    [System.IO.File]::WriteAllText((Join-Path $staging 'extension.vsixmanifest'), $manifest, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $staging '[Content_Types].xml'), $contentTypes, $utf8)

    # Directory.Build.props is the one version this project reports, so the staged manifest gets
    # it whatever the committed package.json says -- and says so when the two disagree, since
    # that is the copy someone installs from a clone.
    $packagePath = Join-Path $staging 'extension/package.json'
    $package     = [System.IO.File]::ReadAllText($packagePath)
    $declared    = [regex]::Match($package, '(?m)^\s*"version"\s*:\s*"(?<value>[^"]*)"').Groups['value'].Value

    if ($declared -ne $Version) {
        Write-Warning "editors/vscode/package.json says $declared; packaging $Version from Directory.Build.props"
    }

    $package = $package -replace '(?m)^(\s*"version"\s*:\s*)"[^"]*"', "`$1`"$Version`""
    [System.IO.File]::WriteAllText($packagePath, $package, $utf8)

    # --- the package ---------------------------------------------------------------------

    $destination = Join-Path $repository $Output
    New-Item -ItemType Directory -Force -Path $destination | Out-Null

    $vsix = Join-Path $destination "chroma-$Version.vsix"
    if (Test-Path $vsix) { Remove-Item -Force $vsix }

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # Not Compress-Archive: entry names are written by hand here, with forward slashes, because
    # a package whose entries carry backslashes is one VS Code refuses to install.
    $zip = [System.IO.Compression.ZipFile]::Open($vsix, 'Create')

    try {
        foreach ($file in Get-ChildItem -Recurse -File $staging) {
            $entry = $file.FullName.Substring($staging.Length + 1).Replace('\', '/')

            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $file.FullName, $entry, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }

    Write-Host "built $Output/$(Split-Path -Leaf $vsix) ($('{0:N0}' -f (Get-Item $vsix).Length) bytes)"

    if ($Install) {
        code --install-extension $vsix --force

        if ($LASTEXITCODE -ne 0) {
            throw "installing $vsix failed"
        }

        Write-Host 'installed -- reload VS Code to pick it up'
    }
}
finally {
    if (Test-Path $staging) {
        Remove-Item -Recurse -Force $staging
    }
}
