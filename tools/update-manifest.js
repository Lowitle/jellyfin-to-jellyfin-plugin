// Maintains the Jellyfin plugin repository manifest (manifest.json). Run by the release workflow, and
// usable locally to preview what a release entry will look like:
//   node tools/update-manifest.js --version 0.1.0.0 --checksum <md5> [--notes "text"] [--dry-run]
const fs = require('fs');
const path = require('path');

const MANIFEST_PATH = path.join(__dirname, '..', 'manifest.json');
const PLUGIN_GUID = '8f3a1c2e-9b4d-4c6a-8e2f-7d1b5a9c3e4f';
const PLUGIN_ARCHIVE = 'jellyfin-to-jellyfin-plugin';

function fail(message) {
    console.error('error: ' + message);
    process.exit(1);
}

function parseArgs(argv) {
    const args = {};
    for (let i = 0; i < argv.length; i++) {
        const token = argv[i];
        if (!token.startsWith('--')) { continue; }
        const key = token.slice(2);
        const next = argv[i + 1];
        if (next === undefined || next.startsWith('--')) {
            args[key] = true;
        } else {
            args[key] = next;
            i++;
        }
    }

    return args;
}

function numericVersion(value) {
    return String(value).split('.').map((part) => parseInt(part, 10) || 0);
}

function compareVersions(a, b) {
    const left = numericVersion(a);
    const right = numericVersion(b);
    for (let i = 0; i < Math.max(left.length, right.length); i++) {
        const diff = (left[i] || 0) - (right[i] || 0);
        if (diff !== 0) { return diff; }
    }

    return 0;
}

const args = parseArgs(process.argv.slice(2));
const version = typeof args.version === 'string' ? args.version.trim() : '';
const checksum = typeof args.checksum === 'string' ? args.checksum.trim().toLowerCase() : '';
const targetAbi = typeof args['target-abi'] === 'string' ? args['target-abi'].trim() : '10.11.6.0';
const repo = typeof args.repo === 'string' ? args.repo.trim() : 'Lowitle/jellyfin-to-jellyfin-plugin';

if (!/^\d+(\.\d+){1,3}$/.test(version)) {
    fail('--version must look like 0.1.0.0 (got "' + version + '").');
}

if (!/^[0-9a-f]{32}$/.test(checksum)) {
    fail('--checksum must be the 32 character MD5 of the release archive (got "' + checksum + '").');
}

if (!fs.existsSync(MANIFEST_PATH)) {
    fail('manifest.json not found at ' + MANIFEST_PATH);
}

const manifest = JSON.parse(fs.readFileSync(MANIFEST_PATH, 'utf8'));
if (!Array.isArray(manifest)) {
    fail('manifest.json must contain a JSON array of plugins.');
}

const plugin = manifest.find((entry) => String(entry.guid).toLowerCase() === PLUGIN_GUID);
if (!plugin) {
    fail('manifest.json has no entry for guid ' + PLUGIN_GUID);
}

const releaseTag = 'v' + version;
const sourceUrl = 'https://github.com/' + repo + '/releases/download/' + releaseTag + '/' + PLUGIN_ARCHIVE + '_' + version + '.zip';
const timestamp = new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
const changelog = typeof args.notes === 'string' && args.notes.trim().length > 0
    ? args.notes.trim()
    : 'Release ' + version;

const entry = {
    version,
    changelog,
    targetAbi,
    sourceUrl,
    checksum,
    timestamp
};

plugin.versions = Array.isArray(plugin.versions) ? plugin.versions : [];
plugin.versions = plugin.versions.filter((item) => item && item.version !== version);
plugin.versions.unshift(entry);
plugin.versions.sort((a, b) => compareVersions(b.version, a.version));

const json = JSON.stringify(manifest, null, 2) + '\n';

if (args['dry-run'] === true) {
    console.log(json);
    process.exit(0);
}

fs.writeFileSync(MANIFEST_PATH, json, 'utf8');
console.log('updated manifest.json: ' + PLUGIN_ARCHIVE + ' ' + version + ' -> ' + sourceUrl);
console.log('checksum: ' + checksum + ' (targetAbi ' + targetAbi + ')');
