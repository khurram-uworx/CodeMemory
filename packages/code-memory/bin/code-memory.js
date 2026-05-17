#!/usr/bin/env node
const { spawn, spawnSync } = require('child_process');
const path = require('path');
const fs = require('fs');

const binDir = __dirname;
const isWin = process.platform === 'win32';
const binaryName = isWin ? 'CodeMemory.Mcp.exe' : 'CodeMemory.Mcp';
const binaryPath = path.join(binDir, binaryName);

if (!fs.existsSync(binaryPath)) {
  console.error(
    `Error: Native binary not found at "${binaryPath}".\n` +
    'The postinstall script should have downloaded it. Try reinstalling:\n' +
    '  npm install @uworx/code-memory\n' +
    'Or reinstall from the package directory:\n' +
    '  node download-binary.js'
  );
  process.exit(1);
}

const result = spawnSync(binaryPath, process.argv.slice(2), {
  stdio: 'inherit',
});

process.exit(result.status ?? 1);
