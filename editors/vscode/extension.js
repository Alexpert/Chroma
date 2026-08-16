// Scene diagnostics in the editor, by running the tool that already produces them.
//
// Chroma.SceneDump takes a scene through the whole front end without a window and writes
// `path:line:column: severity: message` on stderr, which is the conventional compiler format.
// All this file does is run it on open and on save, read those lines back, and put them in the
// Problems panel. There is no language server here, and nothing in the scene language is
// re-implemented in JavaScript: a diagnostic in the editor and a diagnostic in the terminal are
// the same sentence produced by the same code.

const vscode = require('vscode');
const childProcess = require('child_process');
const fs = require('fs');
const path = require('path');

const LANGUAGE = 'chroma';
const TOOL = process.platform === 'win32' ? 'Chroma.SceneDump.exe' : 'Chroma.SceneDump';

// The path group is lazy so it can hold a Windows drive letter's colon: it only settles where
// `:digits:digits:` follows, which no drive letter is.
const DIAGNOSTIC = /^(.+?):(\d+):(\d+):\s+(error|warning):\s+(.*)$/;

/** @type {vscode.DiagnosticCollection} */
let collection;

/** @type {vscode.OutputChannel} */
let output;

// The run in flight per scene, so that holding the save key does not stack processes.
/** @type {Map<string, import('child_process').ChildProcess>} */
const running = new Map();

// Which files each scene last put diagnostics on. An `import` diagnostic names the imported
// file, so one check can report on several, and fixing the fragment has to clear it there.
/** @type {Map<string, string[]>} */
const produced = new Map();

// Where the tool was found, per workspace folder. Cleared when the configuration changes.
/** @type {Map<string, string|null>} */
const resolved = new Map();

// The missing tool is said once per session. Saying it per save would make an editor with no
// Chroma build in reach unusable.
let warnedAboutTool = false;

function activate(context) {
    collection = vscode.languages.createDiagnosticCollection(LANGUAGE);
    output = vscode.window.createOutputChannel('Chroma');

    context.subscriptions.push(
        collection,
        output,
        vscode.workspace.onDidOpenTextDocument(document => check(document)),
        vscode.workspace.onDidSaveTextDocument(document => check(document)),
        vscode.workspace.onDidCloseTextDocument(document => forget(document)),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (!event.affectsConfiguration('chroma')) {
                return;
            }

            resolved.clear();
            warnedAboutTool = false;
            collection.clear();
            produced.clear();

            for (const document of vscode.workspace.textDocuments) {
                check(document);
            }
        }),
        vscode.commands.registerCommand('chroma.checkScene', () => {
            const document = vscode.window.activeTextEditor?.document;

            if (!document || document.languageId !== LANGUAGE) {
                vscode.window.showInformationMessage('Chroma: the active editor is not a scene file.');
                return;
            }

            // Explicitly asked for, so it runs even with diagnostics turned off and says so
            // when the tool cannot be found.
            check(document, true);
        }));

    // Activation happens on the first .chroma document, which is already open by then.
    for (const document of vscode.workspace.textDocuments) {
        check(document);
    }
}

function deactivate() {
    for (const child of running.values()) {
        child.kill();
    }

    running.clear();
}

function check(document, explicit = false) {
    if (document.languageId !== LANGUAGE || document.uri.scheme !== 'file') {
        return;
    }

    const settings = vscode.workspace.getConfiguration('chroma', document.uri);

    if (!explicit && !settings.get('diagnostics.enabled', true)) {
        return;
    }

    const tool = findTool(document, settings);

    if (tool === null) {
        reportMissingTool(explicit);
        return;
    }

    run(document, tool, Math.max(1000, settings.get('diagnostics.timeout', 20000)), explicit);
}

function run(document, tool, timeout, explicit) {
    const scene = document.uri.fsPath;
    const previous = running.get(scene);

    if (previous) {
        previous.kill();
    }

    // The working directory is the scene's own, which is where a relative path written on the
    // command line would have been resolved from; imports resolve against the importing file
    // whatever it is.
    //
    // stdout is the hierarchy dump and can be megabytes on a large scene. It is not read at
    // all rather than read and thrown away, which is what keeps a big scene from costing
    // anything here.
    let child;

    try {
        child = childProcess.spawn(tool, [scene], {
            cwd: path.dirname(scene),
            stdio: ['ignore', 'ignore', 'pipe'],
            windowsHide: true,
        });
    }
    catch (error) {
        reportMissingTool(explicit, tool, error);
        return;
    }

    running.set(scene, child);

    let stderr = '';
    let killed = false;

    child.stderr.setEncoding('utf8');
    child.stderr.on('data', chunk => { stderr += chunk; });

    const timer = setTimeout(() => {
        killed = true;
        child.kill();
    }, timeout);

    child.on('error', error => {
        clearTimeout(timer);

        if (running.get(scene) === child) {
            running.delete(scene);
        }

        reportMissingTool(explicit, tool, error);
    });

    child.on('close', () => {
        clearTimeout(timer);

        // A newer run for this scene owns the result now, and this one's is stale.
        if (running.get(scene) !== child) {
            return;
        }

        running.delete(scene);

        if (killed) {
            output.appendLine(`${scene}: gave up after ${timeout} ms`);
            return;
        }

        publish(scene, stderr);
    });
}

/**
 * Turns the tool's stderr into diagnostics, grouped by the file each one names.
 */
function publish(scene, stderr) {
    const byFile = new Map();

    for (const line of stderr.split(/\r?\n/)) {
        const text = line.trim();

        if (text.length === 0) {
            continue;
        }

        const match = DIAGNOSTIC.exec(text);

        if (!match) {
            // The count that ends a failed load, or a usage error. Neither is a location, and
            // neither belongs in the Problems panel.
            output.appendLine(text);
            continue;
        }

        const file = path.resolve(path.dirname(scene), match[1]);
        const diagnostic = new vscode.Diagnostic(
            rangeAt(file, Number(match[2]) - 1, Number(match[3]) - 1),
            match[5],
            match[4] === 'warning'
                ? vscode.DiagnosticSeverity.Warning
                : vscode.DiagnosticSeverity.Error);

        diagnostic.source = LANGUAGE;

        const existing = byFile.get(file);

        if (existing) {
            existing.push(diagnostic);
        }
        else {
            byFile.set(file, [diagnostic]);
        }
    }

    // The scene itself always gets an answer, empty or not: that is what clears the squiggles
    // of the run before this one.
    if (!byFile.has(scene)) {
        byFile.set(scene, []);
    }

    for (const file of produced.get(scene) ?? []) {
        if (!byFile.has(file)) {
            collection.delete(vscode.Uri.file(file));
        }
    }

    for (const [file, diagnostics] of byFile) {
        collection.set(vscode.Uri.file(file), diagnostics);
    }

    produced.set(scene, [...byFile.keys()]);
}

/**
 * The range to underline. A diagnostic carries a position and no length, so this widens it to
 * the word standing there, which is what the span it came from almost always covers.
 */
function rangeAt(file, line, column) {
    const start = new vscode.Position(Math.max(line, 0), Math.max(column, 0));
    const document = vscode.workspace.textDocuments.find(open => open.uri.fsPath === file);

    if (!document) {
        return new vscode.Range(start, start.translate(0, 1));
    }

    const position = document.validatePosition(start);

    return document.getWordRangeAtPosition(position)
        ?? document.validateRange(new vscode.Range(position, position.translate(0, 1)));
}

function forget(document) {
    const scene = document.uri.fsPath;

    if (!produced.has(scene)) {
        return;
    }

    // Only the scene's own diagnostics go. What it reported about a fragment stays, because the
    // fragment may well be the file still open in front of you.
    collection.delete(document.uri);
}

/**
 * The tool to run, or null when there is none to run.
 */
function findTool(document, settings) {
    const configured = settings.get('sceneDumpPath', '').trim();

    if (configured.length > 0) {
        const folder = vscode.workspace.getWorkspaceFolder(document.uri)
            ?? vscode.workspace.workspaceFolders?.[0];

        const expanded = configured.replace('${workspaceFolder}', folder?.uri.fsPath ?? '');

        return isFile(expanded) ? expanded : null;
    }

    const folder = vscode.workspace.getWorkspaceFolder(document.uri)?.uri.fsPath
        ?? path.dirname(document.uri.fsPath);

    if (resolved.has(folder)) {
        return resolved.get(folder);
    }

    const tool = probe(folder, document.uri.fsPath);
    resolved.set(folder, tool);

    if (tool !== null) {
        output.appendLine(`using ${tool}`);
    }

    return tool;
}

/**
 * Where a build of the tool plausibly is, in the order a scene is plausibly being edited from:
 * a clone of the repository, an unzipped release archive, then whatever is on PATH.
 */
function probe(folder, scene) {
    const candidates = [
        path.join(folder, 'src', 'Chroma.SceneDump', 'bin', 'Debug', 'net8.0', TOOL),
        path.join(folder, 'src', 'Chroma.SceneDump', 'bin', 'Release', 'net8.0', TOOL),
        path.join(folder, TOOL),
    ];

    // A scene opened out of an archive without opening the archive as a folder: scenes/ sits
    // beside the binaries, so the tool is one or two levels up from the file.
    let directory = path.dirname(scene);

    for (let level = 0; level < 3; level++) {
        candidates.push(path.join(directory, TOOL));

        const parent = path.dirname(directory);

        if (parent === directory) {
            break;
        }

        directory = parent;
    }

    for (const candidate of candidates) {
        if (isFile(candidate)) {
            return candidate;
        }
    }

    // Not proof it is there. If it is not, spawn fails with ENOENT and that path reports it.
    return TOOL;
}

function isFile(candidate) {
    try {
        return fs.statSync(candidate).isFile();
    }
    catch {
        return false;
    }
}

function reportMissingTool(explicit, tool, error) {
    if (error) {
        output.appendLine(`could not run ${tool}: ${error.message}`);
    }
    else {
        output.appendLine('no Chroma.SceneDump found; set chroma.sceneDumpPath');
    }

    if (warnedAboutTool && !explicit) {
        return;
    }

    warnedAboutTool = true;

    const setting = 'Set path…';

    vscode.window
        .showWarningMessage(
            'Chroma: cannot find Chroma.SceneDump, so scene files are highlighted but not checked.',
            setting)
        .then(choice => {
            if (choice === setting) {
                vscode.commands.executeCommand('workbench.action.openSettings', 'chroma.sceneDumpPath');
            }
        });
}

module.exports = { activate, deactivate };
