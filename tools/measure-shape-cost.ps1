<#
.SYNOPSIS
    Measures how much geometry this driver will compile, one shape kind at a time.

.DESCRIPTION
    Chroma.Core.Compilation.ShapeCost estimates what a scene weighs, in statements after
    unrolling. The estimate is only useful against a number: how many statements this driver
    will actually take. The driver never says. It answers one bit at a time, by compiling or
    refusing, so that is what this asks it.

    For each shape kind, it generates a scene of N shapes that are DIFFERENT FROM EACH OTHER
    (a repeated shape is shared and costs nothing extra, which is the whole point of
    instancing and would measure nothing here), brackets by doubling, then binary searches
    the largest N that compiles.

    The output is one bracket per kind: the estimate at the largest N that compiled, and the
    estimate at the smallest N that did not. If the model is any good every bracket contains
    one number, and how tightly they agree is its error. That spread is the result, and it is
    reported rather than smoothed over.

    Cheap kinds are measured on top of a fixed base of expensive ones, because four thousand
    spheres take minutes to refuse and the answer is the same.

.PARAMETER Only
    Run just the cases whose name matches this wildcard. Handy while iterating.

.PARAMETER MaxShapes
    Never generate more than this many shapes, whatever the bracket says.

.PARAMETER TimeoutSeconds
    How long one compile may take before it is called a refusal.

.EXAMPLE
    ./tools/measure-shape-cost.ps1
    ./tools/measure-shape-cost.ps1 -Only 'lathe*'
#>

[CmdletBinding()]
param(
    [string] $Exe = "src/Chroma/bin/Release/net8.0/Chroma.exe",
    [string] $Only = "*",
    [int]    $MaxShapes = 3000,
    [int]    $TimeoutSeconds = 300,
    [string] $WorkDir = ""
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $Exe)) {
    throw "no renderer at '$Exe'. Build it first: dotnet build -c Release"
}

if ($WorkDir -eq "") {
    $WorkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("chroma-shape-cost-" + [guid]::NewGuid().ToString("N").Substring(0, 8))
}

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
Write-Host "working in $WorkDir"

# ---------------------------------------------------------------------------------------------
# The shapes.
#
# Each generator returns one solid, and takes an index it MUST make itself distinct by. Varying
# a position would not do it: the canonicaliser normalises position out, which is exactly how a
# chess set shares one pawn, so a thousand spheres at a thousand places are one shape. Varying a
# size does do it.
# ---------------------------------------------------------------------------------------------

function Poly([int] $i, [int] $points, [double] $scale) {
    # A closed outline of $points vertices on a circle, radius nudged by the index.
    $r = $scale + ($i * 0.0001)
    $parts = @()
    for ($k = 0; $k -lt $points; $k++) {
        $a = 2 * [Math]::PI * $k / $points
        $x = [Math]::Round($r * [Math]::Cos($a), 5)
        $y = [Math]::Round($r * [Math]::Sin($a), 5)
        $parts += "$x, $y"
    }
    return ($parts -join ", ")
}

function Outline([int] $i, [int] $points) {
    # A closed outline in (radius, y), which is what a lathe revolves. Starts and ends on the
    # axis so the solid closes.
    $r = 0.6 + ($i * 0.0001)
    $parts = @("0, 0")
    for ($k = 1; $k -lt $points - 1; $k++) {
        $y = [Math]::Round(2.0 * $k / ($points - 1), 5)
        $x = [Math]::Round($r * (0.5 + 0.4 * [Math]::Sin(3.0 * $k)), 5)
        $parts += "$x, $y"
    }
    $parts += "0, 2"
    return ($parts -join ", ")
}

$cases = @(
    @{ Name = "sphere";     Base = 55; Make = { param($i) "sphere { center: [$i, 0, 0], radius: $(0.5 + $i * 0.0001) }" } }
    @{ Name = "box";        Base = 55; Make = { param($i) "box { min: [$i, 0, 0], max: [$($i + 1), $(1 + $i * 0.0001), 1] }" } }
    @{ Name = "cylinder";   Base = 55; Make = { param($i) "cylinder { base: [$i, 0, 0], cap: [$i, 1, 0], radius: $(0.4 + $i * 0.0001) }" } }
    @{ Name = "cone";       Base = 55; Make = { param($i) "cone { base: [$i, 0, 0], baseRadius: $(0.5 + $i * 0.0001), cap: [$i, 1, 0], capRadius: 0.1 }" } }
    @{ Name = "torus";      Base = 55; Make = { param($i) "torus { center: [$i, 1, 0], majorRadius: $(0.8 + $i * 0.0001), minorRadius: 0.3 }" } }
    @{ Name = "difference"; Base = 55; Make = { param($i) "difference { box { min: [$i, 0, 0], max: [$($i + 1), $(1 + $i * 0.0001), 1] } sphere { center: [$($i + 0.5), 1, 0.5], radius: 0.4 } }" } }

    @{ Name = "prism-6";    Base = 0;  Make = { param($i) "prism { bottom: 0, top: 2, points: [$(Poly $i 6 1.0)], translate: [$i, 0, 0] }" } }
    @{ Name = "prism-12";   Base = 0;  Make = { param($i) "prism { bottom: 0, top: 2, points: [$(Poly $i 12 1.0)], translate: [$i, 0, 0] }" } }

    @{ Name = "lathe-6";    Base = 0;  Make = { param($i) "lathe { points: [$(Outline $i 6)], translate: [$i, 0, 0] }" } }
    @{ Name = "lathe-12";   Base = 0;  Make = { param($i) "lathe { points: [$(Outline $i 12)], translate: [$i, 0, 0] }" } }
    @{ Name = "lathe-24";   Base = 0;  Make = { param($i) "lathe { points: [$(Outline $i 24)], translate: [$i, 0, 0] }" } }

    @{ Name = "blob-3";     Base = 0;  Make = { param($i) Blob $i 3 } }
    @{ Name = "blob-8";     Base = 0;  Make = { param($i) Blob $i 8 } }

    @{ Name = "sweep-4";    Base = 0;  Make = { param($i) Sweep $i 4 } }
    @{ Name = "sweep-10";   Base = 0;  Make = { param($i) Sweep $i 10 } }
)

function Blob([int] $i, [int] $components) {
    $body = "blob { threshold: 0.5,"
    for ($k = 0; $k -lt $components; $k++) {
        $r = [Math]::Round(1.0 + ($i * 0.0001) + ($k * 0.05), 5)
        $body += " blobSphere { center: [$($k * 0.4), 0, 0], radius: $r, strength: 1 }"
    }
    return "$body translate: [$i, 1, 0] }"
}

function Sweep([int] $i, [int] $spheres) {
    $parts = @()
    for ($k = 0; $k -lt $spheres; $k++) {
        $r = [Math]::Round(0.3 + ($i * 0.0001) - ($k * 0.02), 5)
        $parts += "$($k * 0.4), $([Math]::Round(0.5 + 0.3 * [Math]::Sin($k), 5)), 0, $r"
    }
    return "sphereSweep { spheres: [$($parts -join ', ')], translate: [$i, 0, 0] }"
}

# The expensive filler that lets a cheap kind be measured without generating thousands of it.
# Twelve-segment lathes, indexed well clear of anything a case will use.
function BaseShapes([int] $count) {
    $lines = @()
    for ($k = 0; $k -lt $count; $k++) {
        $lines += "lathe { points: [$(Outline (100000 + $k) 12)], translate: [0, 0, $(-3 - $k)] }"
    }
    return $lines
}

# ---------------------------------------------------------------------------------------------
# Asking the driver.
# ---------------------------------------------------------------------------------------------

$header = @"
// Generated by tools/measure-shape-cost.ps1. Not a scene anyone should read.
camera { position: [0, 3, 40], lookAt: [0, 1, 0], fov: 50 }
pointLight { position: [0, 12, 12], color: [1, 1, 1], intensity: 400 }

"@

function WriteScene([hashtable] $case, [int] $n, [string] $path) {
    $body = New-Object System.Text.StringBuilder
    [void] $body.Append($header)

    foreach ($line in BaseShapes $case.Base) {
        [void] $body.AppendLine($line)
    }

    for ($i = 0; $i -lt $n; $i++) {
        [void] $body.AppendLine((& $case.Make $i))
    }

    [System.IO.File]::WriteAllText($path, $body.ToString())
}

# Returns @{ Compiles; Estimate }. A refusal still reports an estimate: the console line is
# printed before the shader is handed to the driver, which is the whole reason it is useful.
function Ask([hashtable] $case, [int] $n) {
    $scene = Join-Path $WorkDir "$($case.Name)-$n.chroma"
    $image = Join-Path $WorkDir "out.png"
    WriteScene $case $n $scene

    # System.Diagnostics rather than Start-Process: -PassThru hands back a process object whose
    # ExitCode reads back empty here, and the exit code is the entire measurement.
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = (Resolve-Path $Exe).Path
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true

    # One quoted string rather than an argument list: Windows PowerShell runs on a framework
    # whose ProcessStartInfo has no ArgumentList, and both paths can contain spaces.
    $info.Arguments = '"{0}" --headless --samples 1 --size 64x64 --output "{1}"' -f $scene, $image

    $process = [System.Diagnostics.Process]::Start($info)

    # Read both pipes before waiting. A refused program brings back hundreds of lines of assembly
    # listing on stderr, and a full pipe nobody is draining is a process that never exits.
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill()
        $process.WaitForExit()
        Write-Host "    n=$n timed out after ${TimeoutSeconds}s, counted as refused"
        return @{ Compiles = $false; Estimate = 0; Reason = "timeout" }
    }

    $out = $outTask.Result + $errTask.Result
    $exit = $process.ExitCode

    $estimate = 0
    if ($out -match "estimated (\d+) statements") {
        $estimate = [int] $matches[1]
    }

    # A scene the front end rejects exits the same way a program the driver rejects does, and
    # counting a typo in this script as a measurement of the GPU would poison the whole table.
    # The estimate is printed only once a scene has compiled on this side, so having one is the
    # test.
    if ($exit -ne 0 -and $estimate -eq 0) {
        throw "the generated scene did not compile at all, which is a bug in this script:`n$out"
    }

    Remove-Item $scene -Force
    return @{ Compiles = ($exit -eq 0); Estimate = $estimate; Reason = $out }
}

# ---------------------------------------------------------------------------------------------
# The search.
# ---------------------------------------------------------------------------------------------

$results = @()

foreach ($case in $cases) {
    if ($case.Name -notlike $Only) { continue }

    Write-Host ""
    Write-Host "=== $($case.Name) (base $($case.Base)) ==="

    # A cheap kind is measured on top of a base of expensive ones, and a base that does not
    # itself compile measures nothing at all. Say so rather than reporting a bracket of zero.
    if ($case.Base -gt 0) {
        $floorAnswer = Ask $case 0
        Write-Host ("    base alone: estimate={0} {1}" -f $floorAnswer.Estimate, $(if ($floorAnswer.Compiles) { "compiles" } else { "REFUSED" }))

        if (-not $floorAnswer.Compiles) {
            Write-Host "    the base is already over capacity; lower Base for this case"
            continue
        }
    }

    # Bracket by doubling: find any N that is refused.
    $low = 0            # largest known to compile
    $lowEstimate = 0
    $high = 0           # smallest known to be refused
    $highEstimate = 0
    $n = 4

    while ($true) {
        $answer = Ask $case $n
        Write-Host ("    n={0,-5} estimate={1,-8} {2}" -f $n, $answer.Estimate, $(if ($answer.Compiles) { "compiles" } else { "REFUSED" }))

        if (-not $answer.Compiles) {
            $high = $n
            $highEstimate = $answer.Estimate
            break
        }

        $low = $n
        $lowEstimate = $answer.Estimate

        if ($n -ge $MaxShapes) {
            Write-Host "    still compiling at the cap of $MaxShapes; no upper bracket"
            break
        }

        $n = [Math]::Min($n * 2, $MaxShapes)
    }

    # Binary search the boundary.
    while ($high - $low -gt 1) {
        $n = [int](($low + $high) / 2)
        $answer = Ask $case $n
        Write-Host ("    n={0,-5} estimate={1,-8} {2}" -f $n, $answer.Estimate, $(if ($answer.Compiles) { "compiles" } else { "REFUSED" }))

        if ($answer.Compiles) {
            $low = $n
            $lowEstimate = $answer.Estimate
        }
        else {
            $high = $n
            $highEstimate = $answer.Estimate
        }
    }

    $results += [pscustomobject] @{
        Kind        = $case.Name
        LastOk      = $low
        OkEstimate  = $lowEstimate
        FirstBad    = $high
        BadEstimate = $highEstimate
    }

    Write-Host ("    -> compiles at {0} ({1} statements), refused at {2} ({3})" -f $low, $lowEstimate, $high, $highEstimate)
}

Write-Host ""
Write-Host "=== brackets on the driver's capacity, in statements ==="
$results | Format-Table -AutoSize

$brackets = $results | Where-Object { $_.OkEstimate -gt 0 -and $_.BadEstimate -gt 0 }

if ($brackets.Count -gt 0) {
    $floor = ($brackets | Measure-Object -Property OkEstimate -Maximum).Maximum
    $ceiling = ($brackets | Measure-Object -Property BadEstimate -Minimum).Minimum

    Write-Host ""
    Write-Host "Highest estimate that still compiled: $floor"
    Write-Host "Lowest estimate that was refused:     $ceiling"

    if ($floor -lt $ceiling) {
        Write-Host "Every kind agrees on a capacity between those two. Set ShapeCost.Budget under $floor."
    }
    else {
        Write-Host "The brackets overlap, so the model does not weigh every kind on the same scale."
        Write-Host "The spread is the model's error, and it is the number worth reporting."
    }
}

Remove-Item $WorkDir -Recurse -Force -ErrorAction SilentlyContinue
