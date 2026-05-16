const { platform, arch } = process;
const https = require('https');
const fs = require('fs');
const path = require('path');
const pkg = require('./package.json');

const version = pkg.version;
const repo = 'khurram-uworx/CodeMemory';

// Maps Node.js process platform+arch to .NET runtime identifiers (RIDs)
const ridMap = {
  'win32-x64': 'win-x64',
  // Future: 'linux-x64': 'linux-x64',
  // Future: 'darwin-x64': 'osx-x64',
  // Future: 'darwin-arm64': 'osx-arm64',
};

const key = `${platform}-${arch}`;
const rid = ridMap[key];

if (!rid) {
  console.error(
    `@uworx/code-memory does not support ${platform}-${arch} yet.\n` +
    `Supported platforms: ${Object.keys(ridMap).join(', ') || '(none configured)'}`
  );
  process.exit(1);
}

const binaryName = `code-memory-${rid}.exe`;
const url = `https://github.com/${repo}/releases/download/v${version}/${binaryName}`;
const destDir = path.join(__dirname, 'bin');
const destPath = path.join(destDir, 'code-memory.exe');

// Ensure bin/ exists
fs.mkdirSync(destDir, { recursive: true });

// Temp file for atomic write
const tmpPath = destPath + '.tmp.' + process.pid;

function cleanup() {
  try { fs.unlinkSync(tmpPath); } catch { /* ok */ }
}

console.log(`Downloading CodeMemory v${version} for ${rid}...`);
console.log(`  ${url}`);

const req = https.get(url, (res) => {
  // Follow redirect (GitHub releases uses temporary redirects)
  if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
    console.log(`  Redirected to ${res.headers.location}`);
    https.get(res.headers.location, (res2) => handleResponse(url, res2));
    return;
  }
  handleResponse(url, res);
});

req.on('error', (err) => {
  console.error(`Download failed: ${err.message}`);
  cleanup();
  process.exit(1);
});

function handleResponse(originalUrl, res) {
  if (res.statusCode !== 200) {
    console.error(
      `Download failed: HTTP ${res.statusCode} ${res.statusMessage}\n` +
      `  URL: ${originalUrl}\n` +
      `  Does the release v${version} exist with asset "${binaryName}"?`
    );
    cleanup();
    process.exit(1);
  }

  const file = fs.createWriteStream(tmpPath);
  const total = parseInt(res.headers['content-length'], 10);
  let downloaded = 0;

  res.on('data', (chunk) => {
    downloaded += chunk.length;
    if (total) {
      const pct = ((downloaded / total) * 100).toFixed(1);
      process.stdout.write(`\r  Downloaded ${(downloaded / 1024 / 1024).toFixed(1)}MB / ${(total / 1024 / 1024).toFixed(1)}MB (${pct}%)`);
    }
  });

  res.pipe(file);

  file.on('finish', () => {
    file.close();
    process.stdout.write('\n');

    // Atomic rename
    fs.renameSync(tmpPath, destPath);
    console.log(`  Saved to ${destPath}`);
    console.log('Done.');
  });

  file.on('error', (err) => {
    console.error(`File write error: ${err.message}`);
    cleanup();
    process.exit(1);
  });
}
