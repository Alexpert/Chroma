# Documentation rules

How documentation is written in this repository: who each document is for, what belongs in it,
what never does, and when it is updated. Read this before writing or revising any document.

## Two audiences, never mixed

| Public | Dev |
| --- | --- |
| [README.md](../README.md) | [roadmap.md](roadmap.md) |
| [manual.md](manual.md) | [suggestion.md](suggestion.md) |
| [scene-language.md](scene-language.md) | [current_version.md](current_version.md) |
| [gallery.md](gallery.md) | every other file in `documents/` |

A public document is read by someone who downloaded an archive and wants a picture. A dev
document is read by someone who is going to change the code. The two never share a file.

**A public document never carries** an edge case met during development, a rationale for an
internal decision, a benchmark table, a comparison of approaches, a note about what a driver did
on one machine, or the history of how something came to be. If a reader needs that depth, a link
to the dev document is the whole answer.

**A dev document never doubles as a user guide.** It may quote a scene fragment to make a point;
it does not teach the language.

## Public documents

**[README.md](../README.md)** says what the project is, how to get it, how to run it, and where
to go next. It stays short and readable. A section that grows technical belongs to a dev
document with a one-line pointer left behind. It is updated at the end of every iteration.

**[manual.md](manual.md)** teaches the language in the order a reader meets it, every example
illustrated by the picture it actually produces.

**[scene-language.md](scene-language.md)** is the reference: every node, every field, every
function, findable by name.

**[gallery.md](gallery.md)** shows the sample scenes, one paragraph each.

## The language reference

Anything the language exposes has to be understandable from this document alone. Every entry,
whether it is a node or a function, states:

1. **What it is for**, in one sentence.
2. **What it takes in.** Every field or argument by name, with its type, its unit where it has
   one, its default, and whether it is required. A field left undocumented is a bug in the
   document.
3. **What it gives back.** For a function, the type of the returned value and what it means. For
   a node, what it contributes to the scene and what it accepts as children.
4. **An example**, short enough to read at a glance and valid enough to run.
5. **An illustration**, wherever a picture answers faster than a paragraph. This is the case for
   anything geometric or visual.
6. **What it refuses**, when refusing is part of using it correctly.

No compiler internals, no GLSL, no obscure detail. The reader wants to write a scene. A link to
the dev document is how the curious reader gets the rest, never a substitute for the explanation
itself.

## Illustrations

Images live in `documents/images/<document>/` and are **produced by
[tools/build-manual.ps1](../tools/build-manual.ps1) from the scene they illustrate**, never
placed by hand: that is what keeps `build-manual.ps1 -Check` able to say an image still matches
its file. Reference them with a relative path.

**Illustrations must ship inside the release archives.** A manual whose pictures only load
online is not a manual for someone who unzipped the archive on a machine with no network.
[tools/publish-release.ps1](../tools/publish-release.ps1) copies the public documents and their
images into every archive and rewrites the links that point outside it, then asserts that every
link a shipped document kept relative, images included, resolves inside the folder it just built.
Adding an image to a public document therefore costs nothing, and forgetting to package one fails
the release build instead of reaching a user.

## Dev documents

One document, one subject. `instancing.md` is about instancing; `cutting-unions.md` is about
cutting unions. A subject that grows a second subject becomes a second file with a link between
them, never an appendix bolted onto the first.

Three of them have a fixed role:

**[roadmap.md](roadmap.md)** is the record: what each iteration delivered, what it cost, what it
settled. It may be as detailed and as pointed as it likes. It is updated at the end of every
iteration, and it holds no backlog.

**[suggestion.md](suggestion.md)** is the backlog: everything proposed and not built, by theme.
An entry is **deleted** from it the moment it is scheduled into
[current_version.md](current_version.md), and never lives in both. What it settles is written in
the roadmap when it ships. The list only ever shrinks or gains new proposals.

**[current_version.md](current_version.md)** is what the next delivery contains: the iterations
planned for it and where each one stands. It is kept current while the work happens, not written
up at the end.

## End of an iteration

1. [roadmap.md](roadmap.md) gains the iteration's section: deliverable, what was built, what
   verifies it, what it settled.
2. [suggestion.md](suggestion.md) loses the entries the iteration built, and gains what the work
   proposed and did not do.
3. [current_version.md](current_version.md) marks the iteration done and states what is left
   before the delivery.
4. [README.md](../README.md) is brought back in line with what the program now does, in the
   README's own register: short, and free of anything only a dev cares about.
5. The public documents are updated for anything user-visible that changed, illustrations
   included, and `build-manual.ps1 -Check` passes.

## Every user-visible argument, in four places

A new command line flag, or a change to one, lands in all four or in none:

- the table in [manual.md](manual.md)
- [README.md](../README.md)
- the program's own usage string
- `RUNNING.txt`, generated by `Get-RunningNotes` in
  [tools/publish-release.ps1](../tools/publish-release.ps1)

## House style

Documents are written in **English**, whatever language the work was discussed in. Prose is
neutral and avoids em dashes. Lines wrap at about 95 characters, which is what the existing
corpus does. Links between documents are relative.

These rules apply to what is written from now on. No existing document is rewritten to conform
to them for its own sake; each is brought in line when it is next touched for another reason.
