'use strict';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const root = path.resolve(__dirname, '..');
const out = path.join(root, 'artifacts');
const release = JSON.parse(fs.readFileSync(path.join(out, 'release.json'), 'utf8'));
const npm = process.env.npm_execpath;
if (!npm) throw new Error('Run through npm run smoke:artifacts.');
function run(command, args, cwd) {
  const result = spawnSync(command, args, { cwd, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} failed (${result.status})`);
}
const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'sarif-ratchet-artifacts-'));
try {
  fs.writeFileSync(path.join(temp, 'package.json'), '{"private":true}');
  run(process.execPath, [npm, 'install', '--no-audit', '--no-fund', path.join(out, release.npm)], temp);
  const installed = path.join(temp, 'node_modules', 'sarif-ratchet');
  const manifest = JSON.parse(fs.readFileSync(path.join(installed, 'package.json'), 'utf8'));
  if (manifest.version !== release.version) throw new Error('npm version mismatch');
  for (const name of fs.readdirSync(installed)) {
    if (!['package.json', 'README.md', 'bin'].includes(name)) throw new Error(`Unexpected npm payload: ${name}`);
  }
  for (const rid of ['win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64']) {
    const binary = path.join(installed, 'bin', rid, rid.startsWith('win-') ? 'sarif-ratchet.exe' : 'sarif-ratchet');
    if (!fs.existsSync(binary)) throw new Error(`Missing binary: ${rid}`);
    if (process.platform !== 'win32' && !rid.startsWith('win-')) fs.accessSync(binary, fs.constants.X_OK);
  }
  run(process.execPath, [npm, 'exec', '--offline', '--', 'sarif-ratchet', '--help'], temp);
  const tool = path.join(temp, 'tool');
  const config = path.join(temp, 'NuGet.Config');
  fs.writeFileSync(config, '<configuration><packageSources><clear /></packageSources></configuration>');
  run('dotnet', ['tool', 'install', 'SarifRatchet', '--version', release.version, '--tool-path', tool, '--configfile', config, '--add-source', out], temp);
  run(path.join(tool, process.platform === 'win32' ? 'sarif-ratchet.exe' : 'sarif-ratchet'), ['--help'], temp);
  const rid = `${{ win32: 'win', linux: 'linux', darwin: 'osx' }[process.platform]}-${process.arch}`;
  const binary = path.join(installed, 'bin', rid, process.platform === 'win32' ? 'sarif-ratchet.exe' : 'sarif-ratchet');
  const log = results => ({ version: '2.1.0', runs: [{ tool: { driver: { name: 'ArtifactSmoke' } }, results }] });
  const baseline = path.join(temp, 'baseline.sarif');
  const current = path.join(temp, 'current.sarif');
  fs.writeFileSync(baseline, JSON.stringify(log([])));
  fs.writeFileSync(current, JSON.stringify(log([{
    ruleId: 'R', message: { text: 'Artifact smoke finding' },
    locations: [{ physicalLocation: { artifactLocation: { uri: 'a.cs' }, region: { startLine: 1 } } }]
  }])));
  const args = ['compare', baseline, current, '--format', 'Json'];
  const results = [
    spawnSync(binary, args, { encoding: 'utf8' }),
    spawnSync(process.execPath, [path.join(installed, 'bin', 'index.js'), ...args], { encoding: 'utf8' }),
    spawnSync(path.join(tool, process.platform === 'win32' ? 'sarif-ratchet.exe' : 'sarif-ratchet'), args, { encoding: 'utf8' })
  ];
  for (const result of results) {
    if (result.error) throw result.error;
    if (result.status !== 1) throw new Error(`Packaged comparison must reject the new finding (${result.status})`);
    const report = JSON.parse(result.stdout);
    if (report.NewErrors.length !== 1 || report.NewErrors[0].RuleId !== 'R' || report.NewErrors[0].FilePath !== 'a.cs' ||
        report.FixedErrors.length !== 0 || report.RemainingErrors.length !== 0) throw new Error('Incorrect packaged comparison report');
  }
  console.log(`Validated npm and NuGet ${release.version}: help, payload, new-finding report, and exit propagation.`);
} finally {
  fs.rmSync(temp, { recursive: true, force: true });
}
