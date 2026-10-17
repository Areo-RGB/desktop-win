#!/usr/bin/env node
// Builds FluentDesktop (Debug) and runs it. Windows only.
// Usage: node scripts/debug.js [--no-run] [dotnet build args]
"use strict";

async function main() {
  const { fs, path, IS_WIN, ROOT, run, fail, parseArgs } = await helpers();
  if (!IS_WIN) fail("FluentDesktop is a WinUI app and builds on Windows only.");
  const { flags, rest } = parseArgs();
  const arm = process.arch === "arm64";
  const rid = arm ? "win-arm64" : "win-x64";
  run("dotnet", [
    "build", "FluentDesktop\\FluentDesktop.csproj", "-c", "Debug", "-r", rid,
    `-p:Platform=${arm ? "ARM64" : "x64"}`, ...rest,
  ]);
  if (flags.has("no-run")) return;

  // bin/<platform>/Debug/<tfm>/<rid>/FluentDesktop.exe: pick the newest match.
  const found = [];
  const walk = (dir, depth) => {
    if (depth > 5 || !fs.existsSync(dir)) return;
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(p, depth + 1);
      else if (entry.name === "FluentDesktop.exe" && p.includes(`${path.sep}Debug${path.sep}`) && p.includes(rid)) found.push(p);
    }
  };
  walk(path.join(ROOT, "FluentDesktop", "bin"), 0);
  const exe = found.sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs)[0];
  if (!exe) fail("FluentDesktop.exe not found under FluentDesktop\\bin after the build");
  run(exe, [], { cwd: path.dirname(exe) });
}

// Shared helpers (kept inline so the script has no dependencies). Dynamic
// imports keep it valid under both CommonJS and "type": "module" packages.
async function helpers() {
  const { spawn, spawnSync } = await import("node:child_process");
  const fs = await import("node:fs");
  const path = await import("node:path");
  const os = await import("node:os");
  const IS_WIN = process.platform === "win32";
  const ROOT = path.resolve(path.dirname(path.resolve(process.argv[1])), "..");

  /** Runs a command with inherited stdio; exits the script on failure. */
  function run(cmd, args = [], opts = {}) {
    console.log(`> ${cmd} ${args.join(" ")}`);
    // .cmd/.bat need a shell on Windows; pass one command line (DEP0190).
    const quote = (s) => (/[\s"]/.test(s) ? `"${s.replace(/"/g, '\\"')}"` : s);
    const result = opts.shell
      ? spawnSync([cmd, ...args].map(quote).join(" "), { cwd: ROOT, stdio: "inherit", ...opts })
      : spawnSync(cmd, args, { cwd: ROOT, stdio: "inherit", ...opts });
    if (result.error) fail(`failed to run ${cmd}: ${result.error.message}`);
    if (result.signal) fail(`${cmd} stopped by ${result.signal}`);
    if (result.status !== 0) process.exit(result.status ?? 1);
  }

  /** Runs a command and returns its trimmed stdout ("" on failure). */
  function capture(cmd, args = [], opts = {}) {
    const result = spawnSync(cmd, args, { cwd: ROOT, encoding: "utf8", ...opts });
    return result.status === 0 ? (result.stdout || "").trim() : "";
  }

  function fail(message) {
    console.error(`error: ${message}`);
    process.exit(1);
  }

  /** Splits `--port N` (injected by DesktopCC) and `--flag` switches from the rest. */
  function parseArgs(argv = process.argv.slice(2)) {
    const out = { port: undefined, flags: new Set(), rest: [] };
    for (let i = 0; i < argv.length; i++) {
      const arg = argv[i];
      if (arg === "--port" && i + 1 < argv.length) out.port = argv[++i];
      else if (arg.startsWith("--port=")) out.port = arg.slice(7);
      else if (arg.startsWith("--") && out.rest.length === 0 && arg !== "--") out.flags.add(arg.slice(2));
      else out.rest.push(arg);
    }
    if (out.rest[0] === "--") out.rest.shift();
    return out;
  }

  /** Installs JS dependencies when node_modules is missing. */
  function ensureNodeModules(manager) {
    if (fs.existsSync(path.join(ROOT, "node_modules"))) return;
    run(manager, ["install"], { shell: IS_WIN });
  }

  /** Stops running copies of `exe` (Windows locks running binaries). */
  function stopRunning(exe) {
    if (!IS_WIN || !fs.existsSync(exe)) return;
    const quoted = exe.replace(/'/g, "''");
    spawnSync("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
      `Get-Process | Where-Object { $_.Path -eq '${quoted}' } | ForEach-Object { Write-Output "closing running copy (pid $($_.Id))"; Stop-Process -Id $_.Id -Force; $_.WaitForExit(5000) | Out-Null }`,
    ], { stdio: "inherit" });
  }

  /** Copies a built binary to a stable location so rebuilds don't fight the running app. */
  function installCopy(from, to) {
    if (!fs.existsSync(from)) fail(`built binary not found: ${from}`);
    fs.mkdirSync(path.dirname(to), { recursive: true });
    stopRunning(to);
    if (fs.existsSync(to)) fs.rmSync(to, { force: true });
    fs.copyFileSync(from, to);
    if (!IS_WIN) fs.chmodSync(to, 0o755);
    console.log(`Installed ${to}`);
    return to;
  }

  /** Creates/updates a desktop shortcut (.lnk on Windows, .desktop on Linux). */
  function desktopShortcut({ name, target, icon, wmClass, comment }) {
    if (IS_WIN) {
      const q = (s) => s.replace(/'/g, "''");
      const lnk = capture("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command",
        `$d=[Environment]::GetFolderPath('Desktop'); $l=Join-Path $d '${q(name)}.lnk'; ` +
        `$s=(New-Object -ComObject WScript.Shell).CreateShortcut($l); $s.TargetPath='${q(target)}'; ` +
        `$s.WorkingDirectory='${q(path.dirname(target))}'; $s.IconLocation='${q(icon || target)},0'; ` +
        `$s.Description='${q(comment || name)}'; $s.Save(); $l`,
      ]);
      if (!lnk) fail("could not create the desktop shortcut");
      console.log(`Shortcut: ${lnk}`);
      return;
    }
    const xdg = capture("xdg-user-dir", ["DESKTOP"]);
    const desktop = xdg && path.isAbsolute(xdg) ? xdg : path.join(os.homedir(), "Desktop");
    fs.mkdirSync(desktop, { recursive: true });
    const file = path.join(desktop, `${name.replace(/\s+/g, "")}.desktop`);
    const lines = [
      "[Desktop Entry]", "Type=Application", `Name=${name}`, `Comment=${comment || name}`,
      `Exec="${target}"`, `Path=${path.dirname(target)}`,
      ...(icon && fs.existsSync(icon) ? [`Icon=${icon}`] : []),
      ...(wmClass ? [`StartupWMClass=${wmClass}`] : []),
      "Terminal=false", "",
    ];
    fs.writeFileSync(file, lines.join("\n"), { mode: 0o755 });
    fs.chmodSync(file, 0o755);
    console.log(`Shortcut: ${file}`);
  }

  return { spawn, spawnSync, fs, path, os, IS_WIN, ROOT, run, capture, fail, parseArgs, ensureNodeModules, stopRunning, installCopy, desktopShortcut };
}

main().catch((err) => {
  console.error(err && err.stack ? err.stack : err);
  process.exit(1);
});
