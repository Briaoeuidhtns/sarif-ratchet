#!/usr/bin/env node
'use strict';
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const os = require('node:os');
const platforms = { win32: 'win', linux: 'linux', darwin: 'osx' };
const platform = platforms[process.platform];
if (!platform || !['x64', 'arm64'].includes(process.arch)) {
  console.error(`sarif-ratchet does not support ${process.platform}/${process.arch}.`);
  process.exit(1);
}
const executable = path.join(__dirname, `${platform}-${process.arch}`, process.platform === 'win32' ? 'sarif-ratchet.exe' : 'sarif-ratchet');
const result = spawnSync(executable, process.argv.slice(2), { stdio: 'inherit' });
if (result.error) {
  console.error(`Cannot start sarif-ratchet: ${result.error.message}`);
  process.exit(1);
}
if (result.signal) {
  if (process.platform !== 'win32') process.kill(process.pid, result.signal);
  process.exit(128 + (os.constants.signals[result.signal] || 1));
}
process.exit(result.status ?? 1);
