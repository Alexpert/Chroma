<#
.SYNOPSIS
  Builds the self-contained archives a GitHub release attaches.

.DESCRIPTION
  Each archive holds both programs, the shaders they load at startup, the sample scenes and the
  .NET runtime itself, so whoever downloads it needs nothing installed: unzip and run.

  The upload stays manual. Tag the commit, draft the release on GitHub, paste
  dist/release-notes.md into the form, and attach what this produced.

.PARAMETER Version
  Overrides the version, which is otherwise read from Directory.Build.props.

.PARAMETER Runtime
  Which runtime identifiers to build. Defaults to all four.

.PARAMETER Output
  Where the folders and archives are written. Defaults to dist/, which is not committed.

.PARAMETER NoArchive
  Publishes the folders and stops, which is what to use when testing one runtime by hand.

.EXAMPLE
  powershell -File tools/publish-release.ps1
  powershell -File tools/publish-release.ps1 -Runtime win-x64 -NoArchive
#>

[CmdletBinding()]
param(
    [string]   $Version,
    [string[]] $Runtime = @('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64'),
    [string]   $Output = 'dist',
    [switch]   $NoArchive
)

# Every failure below is a native exit code, checked where it happens. Under 'Stop', Windows
# PowerShell turns a line dotnet writes to stderr into a terminating error.
$ErrorActionPreference = 'Continue'

$repository = Split-Path -Parent $PSScriptRoot

# --- what goes in the archive, and what goes in the release form ----------------------------

# The one file someone reads before double-clicking anything. Per platform, because the awkward
# step is different on each: Windows has none, Linux needs the executable bit back, and macOS
# needs the quarantine flag cleared because these binaries are not signed.
function Get-RunningNotes {
    param([string] $Rid, [string] $Version)

    $lines = @(
        "Chroma $Version -- $Rid",
        '',
        'A GPU ray tracer for constructive solid geometry. Everything needed to run is in this',
        'folder, including the .NET runtime: nothing has to be installed.',
        '',
        'Requires a GPU driver exposing OpenGL 3.3 core or newer.',
        ''
    )

    if ($Rid -like 'win-*') {
        $lines += @(
            'Run it:',
            '',
            '    .\Chroma.exe scenes\cornell.chroma',
            '',
            'The .\ is not decoration. PowerShell never searches the current directory, so the bare',
            'name fails there; cmd.exe accepts the bare name but rejects a forward slash. The',
            'backslash form is the only one both shells run.',
            '',
            'A 1280x720 window opens and the picture converges over a few seconds. Escape closes it.',
            '',
            'Windows SmartScreen may warn that the publisher is unknown, because the executable is',
            'not code-signed: "More info" then "Run anyway".',
            ''
        )
    }
    else {
        $lines += @(
            'The archive was built on Windows, which does not carry the executable bit, so:',
            '',
            '    chmod +x Chroma Chroma.SceneDump'
        )

        if ($Rid -like 'osx-*') {
            $lines += @(
                '',
                'macOS quarantines anything downloaded that is not signed by a registered developer.',
                'These binaries are not signed, so clear the flag once:',
                '',
                '    xattr -dr com.apple.quarantine .',
                '',
                'READ THIS ONE, or the next command prints "killed" and nothing else. This archive is',
                'cross-built on Windows, so its binaries carry no code signature at all, and macOS',
                'refuses to run an unsigned binary on Apple silicon: it kills the process at launch,',
                'with no other message. Signing them yourself, ad-hoc, is what unblocks it. It needs',
                'the Xcode command line tools -- xcode-select --install if you do not have them:',
                '',
                '    find . -type f \( -name "*.dylib" -o -name "Chroma" -o -name "Chroma.SceneDump" \) \',
                '      -exec codesign --force --sign - {} \;',
                '',
                'This is a known limitation of this release rather than something you did wrong. The',
                'real fix is to publish the archive on a Mac, where the .NET SDK signs it in passing,',
                'and that is what a later release will do.'
            )
        }

        $lines += @(
            '',
            'Then:',
            '',
            '    ./Chroma scenes/cornell.chroma',
            '',
            'A 1280x720 window opens and the picture converges over a few seconds. Escape closes it.',
            ''
        )
    }

    if ($Rid -like 'osx-*') {
        $lines += @(
            'On macOS the renderer runs on the OpenGL 3.3 path: Apple caps OpenGL at 4.1, so the',
            'compute path is unavailable and --compute quietly renders on the fragment path.',
            ''
        )
    }

    $lines += @(
        'Options:',
        '',
        '    --samples <n>        stop after n samples per pixel, save a PNG, and close',
        '    --error <percent>    stop at a noise level instead',
        '    --output <path>      write the PNG exactly there',
        '    --size <w>x<h>       framebuffer, instead of 1280x720',
        '    --headless           do not show the window (needs --samples or --error)',
        '    --emit-shader <path> write the GLSL this scene was compiled into',
        '    --no-update-check    do not ask GitHub whether a newer release exists',
        '',
        'The manual lists the rest, which are levers for comparing one rendering path against',
        'another rather than options a picture needs.',
        '',
        'About that last one: an interactive run asks GitHub once a day whether a release newer',
        'than this one exists, and says so with a link if it does. It only ever detects -- nothing',
        'is downloaded and nothing is replaced. The request carries nothing but the version asking,',
        'it runs off the render thread and cannot delay or fail a render, and a run given --samples,',
        '--error, --headless or --output never makes it at all.',
        '',
        'Chroma.SceneDump prints the hierarchy the parser understood, which is what to reach for',
        'when a picture is not what the file said.',
        '',
        'Writing scenes in VS Code: the release also carries an extension for .chroma files,',
        "chroma-$Version.vsix, which colours them and reports their errors in the editor. It runs",
        'Chroma.SceneDump from this folder to do it, so point chroma.sceneDumpPath at it.',
        '',
        "    code --install-extension chroma-$Version.vsix",
        '',
        'The illustrated manual is in this folder, pictures included, and so are the gallery and',
        'the reference for the scene language:',
        '',
        '    documents/manual.md',
        '    documents/gallery.md',
        '    documents/scene-language.md',
        '',
        'The design documents, which are about how the renderer works rather than how to use it,',
        'stayed online:',
        '',
        '    https://github.com/Alexpert/Chroma',
        '',
        'Licensed under the GNU AGPL v3 or later; see LICENSE. The source of this build is at',
        'the address above.'
    )

    return $lines
}

# --- the documents that travel with the binaries ---------------------------------------------

# The public documents go in, the design documents stay out. Someone who unzipped this wants to
# write a scene, and the manual is illustrated: a manual whose pictures only load from GitHub is
# not a manual on a machine with no network. The design documents are for whoever changes the
# code, which is done from a clone rather than from an archive.
$publicDocuments = @('manual.md', 'gallery.md', 'scene-language.md')
$publicImages    = @('manual', 'gallery')

# The archive mirrors the repository for everything it carries -- README.md at the top,
# documents/ and scenes/ beside it -- so a relative link inside a shipped document is still
# correct exactly when what it points at was copied too. That is what this decides, and
# Convert-DocumentLinks rewrites everything else to GitHub.
function Test-ShippedPath {
    param([string] $Path)

    if ($Path -eq 'README.md' -or $Path -eq 'LICENSE') { return $true }
    if ($Path -eq 'scenes' -or $Path -like 'scenes/*') { return $true }

    foreach ($document in $publicDocuments) {
        if ($Path -eq "documents/$document") { return $true }
    }

    foreach ($set in $publicImages) {
        if ($Path -like "documents/images/$set/*") { return $true }
    }

    return $false
}

# 'documents' + '../scenes/manual/' -> 'scenes/manual'. A link is written relative to the file
# holding it; everything above is decided on repository-relative paths, so this is where the two
# meet.
function Resolve-RepositoryPath {
    param([string] $Prefix, [string] $Target)

    $parts = New-Object System.Collections.Generic.List[string]

    foreach ($segment in ("$Prefix/$Target" -split '[\\/]+')) {
        if ($segment -eq '' -or $segment -eq '.') { continue }

        if ($segment -eq '..') {
            if ($parts.Count -gt 0) { $parts.RemoveAt($parts.Count - 1) }
            continue
        }

        $parts.Add($segment)
    }

    return ($parts -join '/')
}

# Every relative link to something the archive does not carry becomes the same file on GitHub, at
# the tag this version is released under. Without it the README inside the archive has three
# broken images and twenty-nine links into a documents/ folder that is not there.
function Convert-DocumentLinks {
    param([string] $Markdown, [string] $Version, [string] $Prefix)

    $base = "https://github.com/Alexpert/Chroma/blob/v$Version"

    # The path inside a markdown ](...) target, minus its anchor. Absolute links and bare
    # anchors are left alone by the lookahead rather than by a test inside the replacement.
    return [regex]::Replace($Markdown, '(?<=\]\()(?!https?:|#)([^)#]+)(#[^)]*)?(?=\))', {
        param($match)

        $path = Resolve-RepositoryPath -Prefix $Prefix -Target $match.Groups[1].Value

        if (Test-ShippedPath $path) {
            return $match.Value
        }

        return "$base/$path$($match.Groups[2].Value)"
    })
}

function Copy-Document {
    param([string] $Source, [string] $Destination, [string] $Version, [string] $Prefix)

    $markdown = [System.IO.File]::ReadAllText($Source)
    $markdown = Convert-DocumentLinks -Markdown $markdown -Version $Version -Prefix $Prefix

    # No BOM: these are read on macOS and Linux terminals as often as in an editor.
    [System.IO.File]::WriteAllText($Destination, $markdown, (New-Object System.Text.UTF8Encoding $false))
}

# Asserted rather than assumed, like the shaders are. A rewritten document is only correct if
# every link it kept relative resolves inside the folder that was just built: an image nobody
# copied, or a scene that was renamed, is a dead link in an archive rather than a failed build.
function Assert-DocumentLinks {
    param([string] $Folder, [string] $Document)

    $markdown = [System.IO.File]::ReadAllText((Join-Path $Folder $Document))
    $prefix   = (Split-Path -Parent $Document) -replace '\\', '/'
    $broken   = @()

    foreach ($match in [regex]::Matches($markdown, '(?<=\]\()(?!https?:|#)([^)#]+)(#[^)]*)?(?=\))')) {
        $path = Resolve-RepositoryPath -Prefix $prefix -Target $match.Groups[1].Value

        if (-not (Test-Path (Join-Path $Folder $path))) {
            $broken += $path
        }
    }

    $broken = @($broken | Select-Object -Unique)

    if ($broken) {
        throw "$Document points at $($broken.Count) file(s) the archive does not carry: $($broken -join ', ')"
    }
}

# --- the release notes ------------------------------------------------------------------------

# Written to dist/ and pasted into GitHub's form. Generated rather than hand-written so the
# version and the archive list cannot disagree with what was actually built.
function Write-ReleaseNotes {
    param([string] $Destination, [string] $Version, [object[]] $Results)

    $rows = ($Results | ForEach-Object { "| ``$($_.Archive)`` | $($_.Runtime) | $($_.Size) |" }) -join "`n"

    # Every backtick in the markdown below is written twice, because a backtick is PowerShell's
    # escape character inside an expandable here-string: a single one is eaten, and a code span
    # comes out as bare text. That is why the fences are six of them rather than three.
    $notes = @"
# Chroma $Version

A GPU ray tracer for **CSG**, constructive solid geometry. You describe a scene in a text file
and it traces rays against the boolean expression directly, so the surfaces are exact and there
is no mesh anywhere in the pipeline. It is a path tracer: light bounces, glass refracts and
throws a caustic, and a solid can hold fog or smoke that light scatters inside.

![A Cornell box with a metal sphere](https://raw.githubusercontent.com/Alexpert/Chroma/v$Version/documents/images/gallery/cornell.png)

## Download

| Archive | Platform | Size |
| --- | --- | --- |
$rows

Each archive carries both programs, the shaders, the sample scenes and the .NET runtime itself.
**Nothing has to be installed**: unzip and run. A GPU driver exposing **OpenGL 3.3 core** is the
only requirement.

## Editing scenes

``chroma-$Version.vsix`` is a VS Code extension for ``.chroma`` files: syntax highlighting, and
the loader's own errors in the Problems panel, with the line and column it already reports. It
gets them by running the ``Chroma.SceneDump`` from the archive above, so point
``chroma.sceneDumpPath`` at it once and every scene you open is checked when you save it.

``````sh
code --install-extension chroma-$Version.vsix
``````

or *Extensions: Install from VSIX* in the command palette. Highlighting works with nothing else
installed.

## Run it

``````sh
.\Chroma.exe scenes\cornell.chroma        # Windows
./Chroma scenes/cornell.chroma            # Linux and macOS
``````

The ``.\`` on Windows is not decoration: PowerShell never searches the current directory, and
cmd.exe rejects a forward slash, so the backslash form is the only one both shells run.

On Linux and macOS the archive was built on Windows, which does not carry the executable bit:

``````sh
chmod +x Chroma Chroma.SceneDump
``````

macOS needs two more steps, and **without the second one the binary is killed at launch**:

``````sh
xattr -dr com.apple.quarantine .
find . -type f \( -name "*.dylib" -o -name "Chroma" -o -name "Chroma.SceneDump" \) \
  -exec codesign --force --sign - {} \;
``````

``RUNNING.txt`` inside each archive says the same thing for that platform.

## Where to start

- [The illustrated manual](https://github.com/Alexpert/Chroma/blob/v$Version/documents/manual.md):
  every feature of the scene language in the order you meet it, with a rendered picture beside
  each example
- [The gallery](https://github.com/Alexpert/Chroma/blob/v$Version/documents/gallery.md): the
  sample scenes in the archive, rendered

## Known limits

- **macOS will not start the binary until you sign it.** Every archive is cross-published from
  Windows, so none of the binaries carries a code signature, and macOS kills an unsigned one on
  Apple silicon at launch: ``killed``, and nothing else. The ad-hoc ``codesign`` above is the
  workaround, and publishing the archive on a Mac is the fix a later release will carry. What is
  past that step has not been seen either: the macOS OpenGL context request is reasoned from
  Apple's documented 4.1 cap rather than measured.
- **The Linux archive has not been launched by anyone.** Windows is the one that was run.
  Reports welcome.
- No nested media, no dispersion, no Russian roulette. The README's *What it does not do* lists
  these with the symptom each produces.
"@

    # Not Set-Content -Encoding utf8: Windows PowerShell writes a BOM with it, which shows up as
    # a stray character when these notes are pasted into GitHub's form.
    [System.IO.File]::WriteAllText($Destination, $notes, (New-Object System.Text.UTF8Encoding $false))
}

# --- the build -------------------------------------------------------------------------------

if (-not $Version) {
    $properties = Join-Path $repository 'Directory.Build.props'
    $match = Select-String -Path $properties -Pattern '<Version>(.+?)</Version>'

    if (-not $match) {
        throw "no <Version> in $properties, and none given with -Version"
    }

    $Version = $match.Matches[0].Groups[1].Value
}

$destination = Join-Path $repository $Output

# `powershell -File script.ps1 -Runtime a,b` hands the whole thing over as one string rather
# than as an array -- only the dot-sourced form parses arrays -- so the commas are split here
# and both spellings work.
$Runtime = $Runtime | ForEach-Object { $_ -split ',' } | Where-Object { $_ }

Write-Host "Chroma $Version -- $($Runtime.Count) runtime(s)"

foreach ($rid in $Runtime) {
    $name   = "chroma-$Version-$rid"
    $folder = Join-Path $destination $name

    if (Test-Path $folder) {
        Remove-Item -Recurse -Force $folder
    }

    Write-Host ""
    Write-Host "=== $rid ==="

    # Both apps go into ONE folder. Published separately they would each carry a copy of the
    # runtime; here they share it, and their .deps.json and .runtimeconfig.json are named after
    # the app so nothing collides.
    #
    # PublishTrimmed stays off, and that is not caution: Silk.NET and ImGui.NET reach their types
    # through reflection and native interop, which a trimmer cannot see. What it removed would
    # fail on the machine of whoever downloaded this, in a build nobody here ran.
    foreach ($project in @('src/Chroma', 'src/Chroma.SceneDump')) {
        Write-Host "publishing $project..."

        dotnet publish (Join-Path $repository $project) `
            -c Release -r $rid --self-contained true `
            -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=none `
            -o $folder -v q --nologo | Out-Null

        if ($LASTEXITCODE -ne 0) {
            throw "publishing $project for $rid failed"
        }
    }

    # Asserted rather than assumed. The shaders are read from disk at startup, so a folder
    # without them is an archive that opens a window and dies on the first frame.
    $executable = if ($rid -like 'win-*') { '.exe' } else { '' }

    foreach ($required in @("Chroma$executable", "Chroma.SceneDump$executable", 'Shaders/raytrace.glsl')) {
        if (-not (Test-Path (Join-Path $folder $required))) {
            throw "$name is missing $required"
        }
    }

    # Something to render on arrival, the licence, and the documents that say what this is and
    # how to write a scene, with the pictures they are illustrated by.
    Copy-Item -Recurse (Join-Path $repository 'scenes') (Join-Path $folder 'scenes')
    Copy-Item (Join-Path $repository 'LICENSE') $folder

    # Copy-Item does not create the parent of its destination, and dotnet publish left none.
    New-Item -ItemType Directory -Force -Path (Join-Path $folder 'documents/images') | Out-Null

    foreach ($set in $publicImages) {
        Copy-Item -Recurse (Join-Path $repository "documents/images/$set") `
                           (Join-Path $folder "documents/images/$set")
    }

    Copy-Document -Source (Join-Path $repository 'README.md') `
                  -Destination (Join-Path $folder 'README.md') -Version $Version -Prefix ''

    foreach ($document in $publicDocuments) {
        Copy-Document -Source (Join-Path $repository "documents/$document") `
                      -Destination (Join-Path $folder "documents/$document") `
                      -Version $Version -Prefix 'documents'
    }

    foreach ($document in (@('README.md') + ($publicDocuments | ForEach-Object { "documents/$_" }))) {
        Assert-DocumentLinks -Folder $folder -Document $document
    }

    # CRLF, because a Windows user opens this in Notepad; no BOM, because a macOS user opens it
    # in a terminal.
    [System.IO.File]::WriteAllText(
        (Join-Path $folder 'RUNNING.txt'),
        ((Get-RunningNotes -Rid $rid -Version $Version) -join "`r`n") + "`r`n",
        (New-Object System.Text.UTF8Encoding $false))

    if ($NoArchive) {
        $results += [pscustomobject]@{ Runtime = $rid; Archive = "$name/ (not archived)"; Size = '' }
        continue
    }

    # .zip on Windows, .tar.gz elsewhere, which is what each platform's users expect to find.
    # Neither carries the executable bit off a Windows machine -- see RUNNING.txt.
    if ($rid -like 'win-*') {
        $archive = Join-Path $destination "$name.zip"
        if (Test-Path $archive) { Remove-Item -Force $archive }

        Compress-Archive -Path $folder -DestinationPath $archive -CompressionLevel Optimal
    }
    else {
        $archive = Join-Path $destination "$name.tar.gz"
        if (Test-Path $archive) { Remove-Item -Force $archive }

        tar -czf $archive -C $destination $name

        if ($LASTEXITCODE -ne 0) {
            throw "archiving $name failed"
        }
    }

    Write-Host "built $(Split-Path -Leaf $archive)"
}

if ($NoArchive) {
    return
}

# The editor extension, which is a deliverable rather than something inside an archive: it is
# installed into VS Code, not unzipped beside the binaries, and one file serves every platform.
# Its name has no runtime in it, which is also what keeps it out of the table below.
Write-Host ""
Write-Host "=== vscode extension ==="

& (Join-Path $PSScriptRoot 'pack-vscode.ps1') -Version $Version -Output $Output

if (-not (Test-Path (Join-Path $destination "chroma-$Version.vsix"))) {
    throw "packing the VS Code extension produced no chroma-$Version.vsix"
}

# Built from what is in dist/ rather than from what this run happened to produce, so that
# building the runtimes one at a time -- which is how a slow cross-publish gets done -- still
# ends with notes listing all four.
$results = Get-ChildItem $destination -File |
    Where-Object { $_.Name -like "chroma-$Version-*.zip" -or $_.Name -like "chroma-$Version-*.tar.gz" } |
    ForEach-Object {
        [pscustomobject]@{
            Runtime = ($_.BaseName -replace "^chroma-$([regex]::Escape($Version))-", '') -replace '\.tar$', ''
            Archive = $_.Name
            Size    = "{0:N1} MB" -f ($_.Length / 1MB)
        }
    }

Write-Host ""
$results | Format-Table -AutoSize

Write-ReleaseNotes -Destination (Join-Path $destination 'release-notes.md') -Version $Version -Results $results
Write-Host "notes written to $Output/release-notes.md -- paste them into the release form"

Write-Host ""
Write-Host "Then: git tag v$Version && git push origin v$Version"
Write-Host "      GitHub -> Releases -> Draft a new release -> attach $Output/*"
