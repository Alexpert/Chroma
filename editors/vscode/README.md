# Chroma for VS Code

Syntax highlighting and scene diagnostics for the `.chroma` files
[Chroma](https://github.com/Alexpert/Chroma) renders.

## What it does

**Highlighting** is a TextMate grammar and nothing else: no process, no language server. It
colours the reserved words, the node types, the built-in functions, the fields, the literals and
both comment forms. `include` and `in` are coloured as deprecated, because they are still
reserved and no longer parse.

**Diagnostics** come from `Chroma.SceneDump`, the program that already takes a scene through the
whole front end without opening a window. The extension runs it when a scene file is opened or
saved, reads the `path:line:column: severity: message` lines it writes, and puts them in the
Problems panel. Nothing about the language is re-implemented here, so an error in the editor is
the same sentence the terminal prints.

An error inside an imported fragment is reported in the fragment, at its own line and column.

**Completion is not part of this.** Useful completion means knowing every node type and its
fields, which is a list that has to be generated from the renderer's own registry rather than
copied into an editor extension.

## Pointing it at Chroma.SceneDump

Highlighting works with nothing installed. Diagnostics need the executable, which the extension
looks for in this order:

1. `chroma.sceneDumpPath`, if set. `${workspaceFolder}` is substituted.
2. `src/Chroma.SceneDump/bin/Debug/net8.0/` then `.../Release/net8.0/` under the workspace
   folder, which is a clone of the repository after `dotnet build`.
3. The workspace folder itself, then up to three folders above the scene file, which is an
   unzipped release archive however it was opened.
4. `PATH`.

If none of those finds it, the extension says so once and goes on highlighting.

## Settings

| Setting | Default | Meaning |
| --- | --- | --- |
| `chroma.sceneDumpPath` | `""` | Path to the executable. Empty means search, as above. |
| `chroma.diagnostics.enabled` | `true` | Check scene files at all. |
| `chroma.diagnostics.timeout` | `20000` | Milliseconds before a check is given up on. |

`Chroma: Check Scene File` in the command palette runs a check on demand, and reports a missing
executable even when checking is turned off.

## Two things to know

**Checks run on save, not as you type.** The tool reads the file from disk, and an unsaved buffer
written to a temporary file elsewhere would break `import`, whose paths resolve relative to the
file that wrote them.

**A fragment several scenes import is reported by whichever was checked last.** The check is per
scene, and one scene has no way of knowing what another one would say about the same fragment.

## Installing

```sh
code --install-extension chroma-<version>.vsix
```

or *Extensions: Install from VSIX…* in the command palette. The `.vsix` is attached to every
[release](https://github.com/Alexpert/Chroma/releases/latest), and
`powershell -File tools/pack-vscode.ps1 -Install` builds and installs it from a clone.

Licensed under the GNU AGPL v3 or later, as the renderer is.
