// Verifies the saved native registration through an isolated MCPHub process.
// Lists tools only; does not execute commands on the SSH target.
import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { homedir } from 'node:os';
import { pathToFileURL } from 'node:url';
const hubRoot = process.env.MCP_HUB_ROOT ?? path.join(homedir(), 'Projects', 'mcp-hub');
const sdk = p => import(pathToFileURL(path.join(hubRoot, 'mcphub_fork', 'node_modules', '@modelcontextprotocol/sdk/dist/esm/client', p)).href);
const { Client } = await sdk('index.js');
const { StreamableHTTPClientTransport } = await sdk('streamableHttp.js');
///@modelcontextprotocol/sdk/dist/esm/client/index.js';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const hubDir = path.join(hubRoot, 'mcphub_fork');
const settings = JSON.parse(await readFile(path.join(hubDir, 'mcp_settings.json'), 'utf8'));
const entry = settings.mcpServers?.['ssh-mcp'];
if (!entry) throw new Error('Save SSH MCP Connection settings in WinUI before verification.');
const directory = await mkdtemp(path.join(tmpdir(), 'winui-ssh-mcp-smoke-'));
const listener = createServer();
await new Promise(resolve => listener.listen(0, '127.0.0.1', resolve));
const port = listener.address().port;
await new Promise(resolve => listener.close(resolve));
const config = path.join(directory, 'mcp_settings.json');
await writeFile(config, JSON.stringify({
  mcpServers: { 'ssh-mcp': entry }, users: [],
  systemConfig: { routing: { enableBearerAuth: false, skipAuth: true } },
}));
let hub;
const client = new Client({ name: 'winui-ssh-mcp-smoke', version: '1.0.0' });
try {
  hub = spawn(process.execPath, ['dist/index.js'], {
    cwd: hubDir, windowsHide: true, stdio: 'ignore',
    env: { ...process.env, PORT: String(port), USE_DB: 'false', MCPHUB_SETTING_PATH: config },
  });
  let ready = false;
  for (let attempt = 0; attempt < 30; attempt++) {
    if (hub.exitCode !== null) throw new Error(`Isolated hub exited (${hub.exitCode}).`);
    try {
      const response = await fetch(`http://127.0.0.1:${port}/health`, { signal: AbortSignal.timeout(1000) });
      if (response.ok) { ready = true; break; }
    } catch {}
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  if (!ready) throw new Error('Isolated hub health check timed out.');
  await client.connect(new StreamableHTTPClientTransport(new URL(`http://127.0.0.1:${port}/mcp/ssh-mcp`)));
  // Hub health can become ready before the upstream stdio handshake finishes.
  let result;
  for (let attempt = 0; attempt < 15; attempt++) {
    result = await client.listTools();
    if (result.tools.some(tool => /run-command$/.test(tool.name))) break;
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  if (!result.tools.length || !result.tools.some(tool => /run-command$/.test(tool.name)))
    throw new Error('SSH endpoint did not expose execution tools: ' + result.tools.map(tool => tool.name).join(', '));
  const listConnections = result.tools.find(tool => /list-connections$/.test(tool.name));
  if (!listConnections) throw new Error('SSH endpoint did not expose connection discovery.');
  const connections = await client.callTool({ name: listConnections.name, arguments: {} });
  if (connections.isError) throw new Error('SSH MCP could not load the migrated connection profile.');
  console.log(`PASS: MCPHub /mcp/ssh-mcp initialized and listed ${result.tools.length} SSH MCP tools.`);
} finally {
  await client.close().catch(() => {});
  if (hub && hub.exitCode === null) {
    if (process.platform === 'win32') spawnSync('taskkill.exe', ['/PID', String(hub.pid), '/T', '/F'], { windowsHide: true, stdio: 'ignore' });
    else hub.kill('SIGTERM');
  }
  // Validate the absolute target before deleting the isolated test directory.
  const relative = path.relative(path.resolve(tmpdir()), path.resolve(directory));
  if (relative.startsWith('winui-ssh-mcp-smoke-') && !relative.includes(path.sep))
    await rm(directory, { recursive: true, force: true });
}
