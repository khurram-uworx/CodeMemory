const { platform, arch } = process;
const https = require('https');
const fs = require('fs');
const path = require('path');
const os = require('os');
const AdmZip = require('adm-zip');

function getVersionsDir() {
  let base;
  if (platform === 'win32') {
    base = process.env.LOCALAPPDATA;
  } else if (platform === 'darwin') {
    base = path.join(os.homedir(), 'Library', 'Application Support');
  } else {
    base = path.join(os.homedir(), '.local', 'share');
  }
  return path.join(base, 'uworx', 'code-memory', 'versions');
}

function semverGt(a, b) {
  const pa = a.split('.').map(Number);
  const pb = b.split('.').map(Number);
  for (let i = 0; i < 3; i++) {
    if ((pa[i] || 0) > (pb[i] || 0)) return true;
    if ((pa[i] || 0) < (pb[i] || 0)) return false;
  }
  return false;
}

function getLatestManifest(versionsDir) {
  try {
    const p = path.join(versionsDir, 'latest.json');
    return JSON.parse(fs.readFileSync(p, 'utf8'));
  } catch {
    return null;
  }
}

function fetchJSON(url) {
  return new Promise((resolve, reject) => {
    https.get(url, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        fetchJSON(res.headers.location).then(resolve, reject);
        return;
      }
      if (res.statusCode !== 200) {
        reject(new Error(`HTTP ${res.statusCode}`));
        return;
      }
      let data = '';
      res.on('data', c => data += c);
      res.on('end', () => {
        try { resolve(JSON.parse(data)); } catch (e) { reject(e); }
      });
    }).on('error', reject);
  });
}

function downloadAndExtract(url, tmpPath, destDir) {
  return new Promise((resolve, reject) => {
    const req = https.get(url, (res) => {
      if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
        https.get(res.headers.location, (res2) => handle(res2)).on('error', reject);
        return;
      }
      handle(res);
    });
    req.on('error', reject);

    function handle(res) {
      if (res.statusCode !== 200) {
        reject(new Error(`HTTP ${res.statusCode}`));
        return;
      }
      const file = fs.createWriteStream(tmpPath);
      res.pipe(file);
      file.on('finish', () => {
        file.close();
        try {
          new AdmZip(tmpPath).extractAllTo(destDir, true);
          fs.rmSync(tmpPath, { force: true });
          resolve();
        } catch (e) {
          reject(e);
        }
      });
      file.on('error', reject);
    }
  });
}

async function checkAndDownloadUpdate({ currentVersion, rid, versionsDir, zipName, repo }) {
  const manifest = getLatestManifest(versionsDir);
  if (manifest && semverGt(manifest.version, currentVersion)) {
    const binName = platform === 'win32' ? 'CodeMemory.Mcp.exe' : 'CodeMemory.Mcp';
    if (fs.existsSync(path.join(versionsDir, `v${manifest.version}`, binName))) {
      return { downloaded: false, version: manifest.version };
    }
  }

  let latest;
  try {
    const reg = await fetchJSON(`https://registry.npmjs.org/@uworx/code-memory/latest`);
    latest = reg.version;
  } catch {
    return { downloaded: false, version: currentVersion };
  }

  if (!semverGt(latest, currentVersion)) {
    return { downloaded: false, version: currentVersion };
  }

  if (manifest && manifest.version === latest) {
    return { downloaded: false, version: latest };
  }

  const zipUrl = `https://github.com/${repo}/releases/download/v${latest}/${zipName}`;
  const destDir = path.join(versionsDir, `v${latest}`);
  const tmpPath = path.join(versionsDir, `${zipName}.tmp.${process.pid}`);

  fs.mkdirSync(destDir, { recursive: true });

  try {
    await downloadAndExtract(zipUrl, tmpPath, destDir);
    fs.writeFileSync(
      path.join(versionsDir, 'latest.json'),
      JSON.stringify({ version: latest, downloadedAt: new Date().toISOString() })
    );
    return { downloaded: true, version: latest };
  } catch {
    try { fs.rmSync(tmpPath, { force: true }); } catch {}
    return { downloaded: false, version: currentVersion };
  }
}

function pruneOldVersions(versionsDir, keepVersion) {
  try {
    const entries = fs.readdirSync(versionsDir, { withFileTypes: true });
    const keepDir = `v${keepVersion}`;
    for (const entry of entries) {
      if (entry.name === keepDir) continue;
      if (entry.name.startsWith('v')) {
        const full = path.join(versionsDir, entry.name);
        fs.rmSync(full, { recursive: true, force: true });
      }
      if (entry.name.endsWith('.tmp')) {
        fs.rmSync(path.join(versionsDir, entry.name), { force: true });
      }
    }
  } catch { /* nothing to prune */ }
}

module.exports = { getVersionsDir, checkAndDownloadUpdate, pruneOldVersions };
