'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
process.chdir(root);
function run(command, args, options = {}) {
  const result = spawnSync(command, args, { encoding: 'utf8', stdio: 'inherit', ...options });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} failed (${result.status})`);
  return result.stdout;
}
// Invoke npm through Node on Windows too; never require a shell or npm.cmd.
const npm = process.env.npm_execpath;
if (!npm) throw new Error('Run through npm run pack:artifacts.');
run('dotnet', ['tool', 'restore']);
const version = run('dotnet', ['nbgv', 'get-version', '--variable', 'NuGetPackageVersion'], { stdio: ['ignore', 'pipe', 'inherit'] }).trim();
if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/.test(version)) throw new Error(`Invalid release version: ${version}`);
if (process.env.GITHUB_REF_TYPE === 'tag' && process.env.GITHUB_REF_NAME !== `v${version}`) throw new Error(`Tag must be v${version}`);
const out = path.join(root, 'artifacts');
fs.mkdirSync(out, { recursive: true });
const stage = fs.mkdtempSync(path.join(out, 'npm-stage-'));
try {
  const manifest = JSON.parse(fs.readFileSync('package.json', 'utf8'));
  manifest.version = version;
  delete manifest.private;
  manifest.scripts = { postinstall: 'node bin/install.cjs' };
  fs.writeFileSync(path.join(stage, 'package.json'), JSON.stringify(manifest, null, 2) + '\n');
  fs.copyFileSync('README.md', path.join(stage, 'README.md'));
  fs.mkdirSync(path.join(stage, 'bin'));
  fs.copyFileSync('bin/index.js', path.join(stage, 'bin/index.js'));
  fs.copyFileSync('bin/install.cjs', path.join(stage, 'bin/install.cjs'));
  fs.chmodSync(path.join(stage, 'bin/index.js'), 0o755);
  for (const rid of ['win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64']) {
    run('dotnet', ['publish', 'src/SarifRatchet/SarifRatchet.csproj', '-c', 'Release', '-f', 'net10.0', '-r', rid, '--self-contained', 'true', `-p:PackageVersion=${version}`, '-o', path.join(stage, 'bin', rid)]);
    if (!rid.startsWith('win-')) fs.chmodSync(path.join(stage, 'bin', rid, 'sarif-ratchet'), 0o755);
  }
  run('dotnet', ['pack', 'src/SarifRatchet/SarifRatchet.csproj', '-c', 'Release', `-p:PackageVersion=${version}`, '-o', out]);
  run(process.execPath, [npm, 'pack', stage, '--pack-destination', out]);
  fs.writeFileSync(path.join(out, 'release.json'), JSON.stringify({ version, npm: `sarif-ratchet-${version}.tgz`, nuget: `SarifRatchet.${version}.nupkg` }, null, 2) + '\n');
} finally {
  fs.rmSync(stage, { recursive: true, force: true });
}
