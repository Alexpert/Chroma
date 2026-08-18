<#
.SYNOPSIS
  Renders the illustrations of the manual and of the reference, and the gallery, from the
  scenes they document.

.DESCRIPTION
  The manual is illustrated, and a picture that no longer matches the file beside it is worse
  than no picture at all. Every image here is produced FROM the scene the manual quotes, by
  this script, so keeping them in step is a command rather than a discipline.

  Renders are reproducible: the sampler is seeded from the pixel and the frame index, so a
  fixed sample count at a fixed size gives the same PNG byte for byte on the same driver.
  That is what -Check relies on.

.PARAMETER Check
  Renders to a temporary directory and compares against what is committed. Non-zero exit on
  any difference, and nothing under documents/images is touched.

.PARAMETER Verify
  Loads every example scene through Chroma.SceneDump, and checks that every fragment the
  manual and the reference documents quote still appears verbatim in the file it says it came
  from. Renders nothing.

.PARAMETER Only
  Renders the entries whose name matches this wildcard, instead of all of them.

.EXAMPLE
  powershell -File tools/build-manual.ps1
  powershell -File tools/build-manual.ps1 -Only material-*
  powershell -File tools/build-manual.ps1 -Check
  powershell -File tools/build-manual.ps1 -Verify
#>

[CmdletBinding()]
param(
    [switch] $Check,
    [switch] $Verify,
    [string] $Only = '*'
)

# Deliberately not 'Stop'. Every failure here is a native exit code, which is checked where it
# happens; under 'Stop', Windows PowerShell turns a line written to stderr by dotnet into a
# terminating error and the script dies on a warning it was meant to print.
$ErrorActionPreference = 'Continue'

$repository = Split-Path -Parent $PSScriptRoot
$scenes     = Join-Path $repository 'scenes/manual'
$plates     = Join-Path $repository 'scenes/reference'
$images     = Join-Path $repository 'documents/images/manual'
$reference  = Join-Path $repository 'documents/images/reference'
$gallery    = Join-Path $repository 'documents/images/gallery'
$manual     = Join-Path $repository 'documents/manual.md'

# The documents whose quoted fragments -Verify checks. The manual teaches in order and the four
# reference documents are looked things up in, but both quote scene files the same way.
$documents = @(
    $manual,
    (Join-Path $repository 'documents/scene-language.md'),
    (Join-Path $repository 'documents/scene-primitives.md'),
    (Join-Path $repository 'documents/scene-composition.md'),
    (Join-Path $repository 'documents/scene-appearance.md')
)

# 640 wide is enough to show a shape and its shadow, and this repository is otherwise text:
# sixty illustrations at 1280 would outweigh everything else in it several times over.
$width  = 640
$height = 360

# Sample counts are per scene rather than one flat number, because what an image needs is a
# property of the image. Each was found by rendering with --error until the noise stopped
# being visible, and then written down: a fixed count is what makes a re-run a byte
# comparison rather than a race against a threshold.
#
# The expensive ones are expensive for a reason worth knowing. A medium spends bounces on
# scattering vertices, and an emissive solid is never sampled directly -- it is found only
# when a bounce happens to land on it, which is the slowest way to be lit.
$manualScenes = [ordered]@{
    'first-scene'               = 1500
    'coordinates'               = 1500
    'camera-fov-narrow'         = 2000
    'camera-fov-wide'           = 2000
    'light-radius-hard'         = 2000
    'light-radius-soft'         = 3000
    'light-directional'         = 1500
    'light-falloff'             = 3000
    'material-roughness'        = 3000
    'material-metallic'         = 3000
    'material-emission'         = 20000
    'material-transmission'     = 4000
    'material-ior'              = 5000
    'material-absorption'       = 4000
    'medium-scattering'         = 6000
    'medium-anisotropy-none'    = 12000
    'medium-anisotropy-forward' = 12000
    'primitives-basic'          = 2000
    'primitives-more'           = 3000
    'primitive-plane'           = 2500
    'primitive-lathe'           = 3000
    'primitive-spheresweep'     = 2500
    'primitive-contours'        = 2500
    'primitive-blobcylinder'    = 3000
    'primitive-quadric'         = 2500
    'primitive-mesh'            = 2500
    'primitive-heightfield'     = 2500
    'csg-operators'             = 2500
    'transforms'                = 2000
    'object-binding'            = 3000
    'union-vs-top-level'        = 6000
    'loop-grid'                 = 3000
    'function-row'              = 2500
    'checkerboard'              = 2500
    'include-palette'           = 2500
}

# The reference documents illustrate one thing per plate: a primitive on its own, or one option
# against the same shape without it. They are rendered from scenes/reference and land in
# documents/images/reference.
$referenceScenes = [ordered]@{
    'sphere'                      = 2000
    'box'                         = 2000
    'cylinder'                    = 2000
    'cone'                        = 2000
    'plane'                       = 2500
    'torus'                       = 2500
    'prism'                       = 2500
    'prism-spline'                = 2500
    'lathe'                       = 3000
    'spline-steps'                = 3000
    'spheresweep'                 = 2500
    'spheresweep-spline'          = 2500
    'quadric'                     = 2500
    'blob'                        = 3000
    'blob-threshold'              = 3000
    'blob-strength'               = 3000
    'blob-cylinder'               = 3000
    'mesh'                        = 2500
    'mesh-smooth'                 = 2500
    'heightfield'                 = 2500
    'heightfield-heights'         = 2000
    'heightfield-resolution'      = 2500
    'heightfield-base'            = 2500
    'heightfield-smooth'          = 2500
    'csg-difference-order'        = 2500
    'modifier-scale'              = 2000
    'material-inheritance'        = 2500
    'material-color'              = 2500
    'material-metallic-roughness' = 4000
    'light-color'                 = 2500
    'camera-up-level'             = 1500
    'camera-up-rolled'            = 1500
    'render-exposure-low'         = 2000
    'render-exposure-high'        = 2000
    'render-bounces-one'          = 3000
    'render-bounces-many'         = 6000
    'seed-three'                  = 2000
    'seed-nine'                   = 2000
    'arrays-structs'              = 2500
    'recursion'                   = 3000

    # Two lenses of the same shape at two indices, over a chequered floor: what the camera sees
    # through each of them is the whole of the picture, so it converges quickly.
    'ior-lens'                    = 3000
}

# The gallery argues for the project in ten seconds, and it does it with the scenes that were
# already there: these are the deliverables of the iterations that built them.
$galleryScenes = [ordered]@{
    'cornell'    = 6000
    'glass'      = 20000
    'fog'        = 12000
    'shapes'     = 4000
    'sweeps'     = 4000
    'meshes'     = 4000
    'terrain'    = 4000
    'lattice'    = 6000
    'colonnade'  = 4000

    # chess-half rather than chess-full, and this used to be forced: the full set generated 7434
    # lines of GLSL and the driver refused a fragment program that large. Instancing ended that --
    # thirty-two pieces reach the ray through ten shapes, and chess-full compiles and renders.
    # The half board is kept because it is the better picture at this size, which is now a matter
    # of taste rather than a limit. See documents/instancing.md.
    'chess-half' = 6000
}

function Get-Entries {
    $entries = @()

    foreach ($name in $manualScenes.Keys) {
        $entries += [pscustomobject]@{
            Name    = $name
            Scene   = Join-Path $scenes "$name.chroma"
            Target  = Join-Path $images "$name.png"
            Samples = $manualScenes[$name]
        }
    }

    foreach ($name in $referenceScenes.Keys) {
        $entries += [pscustomobject]@{
            Name    = $name
            Scene   = Join-Path $plates "$name.chroma"
            Target  = Join-Path $reference "$name.png"
            Samples = $referenceScenes[$name]
        }
    }

    foreach ($name in $galleryScenes.Keys) {
        $entries += [pscustomobject]@{
            Name    = $name
            Scene   = Join-Path $repository "scenes/$name.chroma"
            Target  = Join-Path $gallery "$name.png"
            Samples = $galleryScenes[$name]
        }
    }

    return $entries | Where-Object { $_.Name -like $Only }
}

function Invoke-Build {
    Write-Host 'building Chroma...'
    dotnet build (Join-Path $repository 'src/Chroma') -v q --nologo | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw 'the build failed; nothing was rendered'
    }
}

function Invoke-Render {
    param([string] $Scene, [string] $Target, [int] $Samples)

    $directory = Split-Path -Parent $Target
    if (-not (Test-Path $directory)) {
        New-Item -ItemType Directory -Force $directory | Out-Null
    }

    # --headless keeps sixty renders from taking the screen for a minute; --output is what
    # makes the destination the same on every run, where the interactive save deliberately
    # dates its files instead.
    $output = dotnet run --project (Join-Path $repository 'src/Chroma') --no-build -- `
        $Scene --samples $Samples --size "${width}x${height}" --headless --output $Target 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Host ($output | Out-String)
        throw "rendering $Scene failed"
    }
}

# --- -Verify --------------------------------------------------------------------------------

# Every fenced block in the manual or in a reference document that claims to come from a scene
# is checked against that scene, line by line. A document quotes fragments rather than whole
# files -- a reader wants the six lines that matter -- and this is what stops a fragment
# drifting away from the file it was taken from.
function Test-Quotations {
    $bad = 0

    foreach ($document in $documents) {
        $bad += Test-Document $document
    }

    return $bad
}

function Test-Document {
    param([string] $Document)

    if (-not (Test-Path $Document)) {
        Write-Host "  MISSING $Document"
        return 1
    }

    $text = Get-Content $Document -Raw
    $bad  = 0

    $pattern = '(?ms)<!--\s*from:\s*(?<file>[^\s]+)\s*-->\r?\n```[a-z]*\r?\n(?<body>.*?)```'

    foreach ($match in [regex]::Matches($text, $pattern)) {
        $file = Join-Path $repository $match.Groups['file'].Value

        if (-not (Test-Path $file)) {
            Write-Host "  MISSING $($match.Groups['file'].Value)"
            $bad++
            continue
        }

        # Compared with the indentation removed from both sides. A quoted fragment is lifted
        # out of whatever block it sat in and re-indented to read on its own, so leading
        # whitespace is the one difference that carries no meaning; everything else does.
        $source = (Get-Content $file) | ForEach-Object { $_.Trim() }

        foreach ($line in $match.Groups['body'].Value -split "\r?\n") {
            $trimmed = $line.Trim()

            # Blank lines and an explicit elision are how a fragment is shortened; neither
            # claims to be in the file.
            if ($trimmed.Length -eq 0 -or $trimmed -eq '// ...') {
                continue
            }

            if ($source -notcontains $trimmed) {
                Write-Host "  DRIFTED $($match.Groups['file'].Value): $trimmed"
                $bad++
            }
        }
    }

    return $bad
}

function Test-Scenes {
    $bad = 0

    $files = @(Get-ChildItem (Join-Path $scenes '*.chroma')) +
             @(Get-ChildItem (Join-Path $plates '*.chroma'))

    foreach ($scene in $files) {
        $output = dotnet run --project (Join-Path $repository 'src/Chroma.SceneDump') --no-build -- `
            $scene.FullName 2>&1

        # The palette fragment declares materials and no camera, which is what a fragment is;
        # it is loaded by include-palette.chroma rather than on its own.
        if ($LASTEXITCODE -ne 0 -and $scene.Name -ne 'palette.chroma') {
            Write-Host "  FAILED $($scene.Name)"
            Write-Host ($output | Select-Object -Last 5 | Out-String)
            $bad++
        }
    }

    return $bad
}

if ($Verify) {
    Invoke-Build

    Write-Host 'loading every example scene...'
    $broken = Test-Scenes

    Write-Host 'checking the fragments the documents quote...'
    $drifted = Test-Quotations

    if ($broken -eq 0 -and $drifted -eq 0) {
        Write-Host 'verify: every scene loads and every quotation matches its file'
        exit 0
    }

    Write-Host "verify: $broken scene(s) failed to load, $drifted quoted line(s) drifted"
    exit 1
}

# --- -Check and the default -----------------------------------------------------------------

Invoke-Build

$entries = Get-Entries
$total   = ($entries | Measure-Object).Count
$index   = 0
$differ  = @()

$temporary = Join-Path ([System.IO.Path]::GetTempPath()) ("chroma-manual-" + [guid]::NewGuid().ToString('n'))

foreach ($entry in $entries) {
    $index++
    $label = "[{0,2}/{1}] {2}" -f $index, $total, $entry.Name

    if ($Check) {
        $candidate = Join-Path $temporary "$($entry.Name).png"
        Invoke-Render -Scene $entry.Scene -Target $candidate -Samples $entry.Samples

        if (-not (Test-Path $entry.Target)) {
            Write-Host "$label  MISSING"
            $differ += $entry.Name
            continue
        }

        $fresh     = (Get-FileHash $candidate).Hash
        $committed = (Get-FileHash $entry.Target).Hash

        if ($fresh -eq $committed) {
            Write-Host "$label  same"
        }
        else {
            Write-Host "$label  DIFFERS"
            $differ += $entry.Name
        }

        continue
    }

    Invoke-Render -Scene $entry.Scene -Target $entry.Target -Samples $entry.Samples
    Write-Host "$label  $($entry.Samples) samples"
}

if (Test-Path $temporary) {
    Remove-Item -Recurse -Force $temporary
}

if (-not $Check) {
    $bytes = (Get-ChildItem (Join-Path $repository 'documents/images') -Recurse -Filter *.png |
        Measure-Object -Property Length -Sum).Sum

    Write-Host ("done: {0} images, {1:N1} MB under documents/images" -f $total, ($bytes / 1MB))
    exit 0
}

if ($differ.Count -eq 0) {
    Write-Host "check: all $total images are byte-identical to what is committed"
    exit 0
}

Write-Host "check: $($differ.Count) of $total differ -- $($differ -join ', ')"
exit 1
