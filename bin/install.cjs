'use strict';
const fs = require('node:fs');
const path = require('node:path');

// Windows cannot preserve POSIX executable bits when creating an npm tarball.
// Apply them on the installation host, while the package directory is writable.
if (process.platform !== 'win32') {
  for (const rid of ['linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64']) {
    fs.chmodSync(path.join(__dirname, rid, 'sarif-ratchet'), 0o755);
  }
}
