import { appendFile, readFile, readdir, writeFile } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { join, resolve } from 'node:path';

const root = fileURLToPath(new URL('../', import.meta.url));
const repository = 'meshline-network/sdk';
const registry = 'https://registry.npmjs.org';
export const packageDirectories = ['sdk', 'storage-node', 'storage-browser', 'transport-node', 'expo'];
const number = '(0|[1-9][0-9]*)';
const identifier = '(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)';
const semver = new RegExp(`^${number}\\.${number}\\.${number}(?:-(${identifier}(?:\\.${identifier})*))?$`);

export function releaseVersion(version) {
    const match = typeof version === 'string' && semver.exec(version);
    if (!match) throw new Error('Use canonical SemVer without build metadata.');
    const distTag = match[4]?.split('.')[0] ?? 'latest';
    if (match[4] && !['alpha', 'beta', 'rc'].includes(distTag)) throw new Error('Prereleases must use alpha, beta, or rc.');
    return { version, tag: `typescript-v${version}`, distTag, prerelease: Boolean(match[4]) };
}

export function compareVersions(left, right) {
    const a = semver.exec(left); const b = semver.exec(right);
    if (!a || !b) throw new Error('Cannot compare a non-canonical registry version.');
    for (let i = 1; i <= 3; i++) if (BigInt(a[i]) !== BigInt(b[i])) return BigInt(a[i]) > BigInt(b[i]) ? 1 : -1;
    if (!a[4] || !b[4]) return a[4] === b[4] ? 0 : a[4] ? -1 : 1;
    const ap = a[4].split('.'); const bp = b[4].split('.');
    for (let i = 0; i < Math.max(ap.length, bp.length); i++) {
        if (ap[i] === bp[i]) continue;
        if (ap[i] === undefined || bp[i] === undefined) return ap[i] === undefined ? -1 : 1;
        const an = /^\d+$/.test(ap[i]); const bn = /^\d+$/.test(bp[i]);
        if (an && bn) return BigInt(ap[i]) > BigInt(bp[i]) ? 1 : -1;
        if (an !== bn) return an ? -1 : 1;
        return ap[i] > bp[i] ? 1 : -1;
    }
    return 0;
}

export function validatePackages(manifests, lock) {
    if (manifests.length !== packageDirectories.length) throw new Error('Expected exactly five release packages.');
    const info = releaseVersion(manifests[0].version);
    for (const [i, manifest] of manifests.entries()) {
        const directory = packageDirectories[i]; const name = `@meshline/${directory}`;
        if (manifest.name !== name || manifest.version !== info.version || manifest.private) throw new Error(`Invalid name/version/publication status: ${name}`);
        if (manifest.repository?.url !== `https://github.com/${repository}.git` || manifest.repository?.directory !== `typescript/packages/${directory}`) throw new Error(`Invalid repository metadata: ${name}`);
        if (manifest.publishConfig?.access !== 'public' || manifest.publishConfig?.registry !== registry) throw new Error(`Invalid publication configuration: ${name}`);
        const locked = lock.packages?.[`packages/${directory}`];
        if (locked?.version !== info.version) throw new Error(`Update package-lock.json for ${name}.`);
        if (i && manifest.peerDependencies?.['@meshline/sdk'] !== info.version) throw new Error(`Pin the core peer version in ${name}.`);
        for (const field of ['dependencies', 'peerDependencies', 'optionalDependencies', 'devDependencies']) {
            for (const [dependency, version] of Object.entries(manifest[field] ?? {})) {
                if (!dependency.startsWith('@meshline/')) continue;
                if (!packageDirectories.some(value => dependency === `@meshline/${value}`) || version !== info.version || locked[field]?.[dependency] !== version) throw new Error(`Mismatched internal dependency in ${name}: ${dependency}`);
            }
        }
    }
    return info;
}

export function validateRelease(release, info, sha, tagCommit) {
    // Later main commits with an unchanged version must not republish it.
    if (release && !release.draft) return false;
    if (release && release.target_commitish !== sha) throw new Error(`Draft ${info.tag} belongs to another commit; rerun the original workflow run.`);
    if (tagCommit && tagCommit !== sha) throw new Error(`Tag ${info.tag} points to another commit.`);
    return true;
}

const digest = (algorithm, bytes, encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
export async function verifyPackages(info, directory) {
    const manifest = JSON.parse(await readFile(join(directory, 'manifest.json'), 'utf8'));
    if (manifest.packages?.length !== packageDirectories.length) throw new Error('Incomplete package manifest.');
    const packages = [];
    for (const name of packageDirectories.map(value => `@meshline/${value}`)) {
        const matches = manifest.packages.filter(entry => entry.name === name);
        const entry = matches[0];
        const filename = `${name.replace('@', '').replace('/', '-')}-${info.version}.tgz`;
        if (matches.length !== 1 || entry.version !== info.version || entry.filename !== filename) throw new Error(`Invalid packed identity: ${name}`);
        const bytes = await readFile(join(directory, filename));
        if (digest('sha256', bytes) !== entry.sha256 || `sha512-${digest('sha512', bytes, 'base64')}` !== entry.integrity) throw new Error(`Package checksum differs: ${name}`);
        packages.push({ ...entry, path: join(directory, filename) });
    }
    return packages;
}

export async function publicationPlan(packages, info, lookup) {
    // Check every package before uploading any: a collision must not cause a
    // partial release. Identical bytes allow a failed run to resume safely.
    const pending = [];
    for (const entry of packages) {
        const metadata = await lookup(entry.name);
        const existing = metadata?.versions?.[info.version];
        if (existing && existing.dist?.integrity !== entry.integrity) throw new Error(`${entry.name}@${info.version} already exists with different bytes. Bump all package versions.`);
        const current = metadata?.['dist-tags']?.[info.distTag];
        if (current && compareVersions(current, info.version) > 0) throw new Error(`Refusing to move ${entry.name} ${info.distTag} backward from ${current}.`);
        if (existing && current !== info.version) throw new Error(`${entry.name}@${info.version} exists but ${info.distTag} does not point to it. Inspect npm tags before retrying.`);
        if (!existing) pending.push(entry);
    }
    return pending;
}

function run(command, args) {
    const result = spawnSync(command, args, { cwd: root, encoding: 'utf8', windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    if (result.error) throw result.error;
    if (result.status !== 0) throw new Error(`${command} failed (${result.status}): ${result.stderr || result.stdout}`);
    if (result.stderr) process.stderr.write(result.stderr);
    return result.stdout.trim();
}

export async function setWorkspaceVersion(version, { directory = root, npm = process.env.npm_execpath, execute = run } = {}) {
    releaseVersion(version);
    if (!npm) throw new Error('Run with npm run release -- version <version>.');
    const files = packageDirectories.map(name => join(directory, 'packages', name, 'package.json'));
    const contents = await Promise.all(files.map(file => readFile(file, 'utf8')));
    const manifests = contents.map(JSON.parse);
    // Validate every manifest before writing, and leave unrelated dependencies alone.
    for (const [i, manifest] of manifests.entries()) {
        if (manifest.name !== `@meshline/${packageDirectories[i]}` || manifest.private) throw new Error(`Unexpected release package: ${manifest.name}`);
        manifest.version = version;
        for (const field of ['dependencies', 'peerDependencies', 'optionalDependencies', 'devDependencies']) {
            for (const dependency of Object.keys(manifest[field] ?? {})) {
                if (!dependency.startsWith('@meshline/')) continue;
                if (!packageDirectories.some(name => dependency === `@meshline/${name}`)) throw new Error(`Unknown internal dependency: ${dependency}`);
                manifest[field][dependency] = version;
            }
        }
    }
    for (const [i, manifest] of manifests.entries()) {
        const text = JSON.stringify(manifest, null, 2) + '\n';
        if (text !== contents[i]) await writeFile(files[i], text);
    }
    for (const prefix of [directory, resolve(directory, '../tests/interop/typescript')]) {
        execute(process.execPath, [npm, 'install', '--package-lock-only', '--ignore-scripts', '--no-audit', '--no-fund', '--prefix', prefix]);
    }
}

async function json(url, authenticated = false) {
    const headers = authenticated ? { Authorization: `Bearer ${process.env.GH_TOKEN}`, Accept: 'application/vnd.github+json' } : {};
    const response = await fetch(url, { headers, signal: AbortSignal.timeout(30_000), redirect: 'error' });
    if (response.status === 404 && !authenticated) return null;
    if (!response.ok) throw new Error(`Lookup failed: HTTP ${response.status} at ${url}`);
    return response.json();
}

async function findRelease(tag) {
    // Listing includes drafts for the publishing token and covers older pages.
    for (let page = 1; ; page++) {
        const releases = await json(`https://api.github.com/repos/${repository}/releases?per_page=100&page=${page}`, true);
        const found = releases.find(value => value.tag_name === tag);
        if (found) return found;
        if (releases.length < 100) return null;
    }
}

export async function publishRelease({ info, sha, release, packages, lookup, execute, npm, directory, wait = ms => new Promise(resolve => setTimeout(resolve, ms)) }) {
    const pending = await publicationPlan(packages, info, lookup);
    const checksums = join(directory, 'SHA256SUMS');
    await writeFile(checksums, packages.map(entry => `${entry.sha256}  ${entry.filename}\n`).join(''));
    const gh = args => execute('gh', [...args, '--repo', repository]);
    if (!release) {
        // Reserve the source commit before the first immutable npm upload.
        gh(['release', 'create', info.tag, '--draft', '--target', sha, '--title', `TypeScript SDK ${info.version}`, '--generate-notes', `--prerelease=${info.prerelease}`, '--latest=false']);
    }
    gh(['release', 'upload', info.tag, ...packages.map(entry => entry.path), checksums, '--clobber']);
    for (const entry of pending) {
        execute(process.execPath, [npm, 'publish', entry.path, '--access=public', `--tag=${info.distTag}`, `--registry=${registry}`, '--provenance', '--ignore-scripts']);
    }
    // npm scans uploads before making them available. Poll the whole release
    // together so the wait budget does not multiply by the number of packages.
    let remaining = packages;
    for (let attempt = 0; attempt <= 40; attempt++) {
        const unavailable = [];
        for (const entry of remaining) {
            const metadata = await lookup(entry.name);
            const published = metadata?.versions?.[info.version];
            if (published && published.dist?.integrity !== entry.integrity) throw new Error(`Published integrity mismatch: ${entry.name}`);
            if (!published || metadata['dist-tags']?.[info.distTag] !== info.version) unavailable.push(entry);
        }
        remaining = unavailable;
        if (!remaining.length) break;
        if (attempt === 40) throw new Error(`npm has not indexed ${remaining.map(entry => entry.name).join(', ')} yet; inspect npm status and rerun this workflow run after the packages become visible.`);
        console.log(`Waiting for npm availability: ${remaining.map(entry => entry.name).join(', ')}`);
        await wait(30_000);
    }
    // The public release is the completion marker, never a substitute for npm success.
    gh(['release', 'edit', info.tag, '--draft=false', `--prerelease=${info.prerelease}`, '--latest=false']);
}

async function main() {
    const mode = process.argv[2];
    if (mode === 'version' && process.argv.length === 4) {
        await setWorkspaceVersion(process.argv[3]);
        console.log(`Updated five packages and both lockfiles to ${process.argv[3]}. Review the local diff before committing.`);
        return;
    }
    if (!['check', 'publish'].includes(mode) || process.argv.length !== 3) throw new Error('Usage: npm run release -- check|publish or npm run release -- version <version>');
    const sha = process.env.GITHUB_SHA;
    if (process.env.GITHUB_REPOSITORY !== repository || process.env.GITHUB_REF !== 'refs/heads/main' || !/^[a-f0-9]{40}$/.test(sha ?? '') || !process.env.GH_TOKEN) throw new Error('Run this workflow on meshline-network/sdk main with a GitHub token.');
    if (run('git', ['rev-parse', 'HEAD']) !== sha) throw new Error('The checkout does not match GITHUB_SHA.');
    const directories = (await readdir(join(root, 'packages'), { withFileTypes: true })).filter(entry => entry.isDirectory()).map(entry => entry.name);
    if (directories.length !== packageDirectories.length || directories.some(name => !packageDirectories.includes(name))) throw new Error('Review the release package list after adding/removing a workspace.');
    const manifests = await Promise.all(packageDirectories.map(directory => readFile(join(root, 'packages', directory, 'package.json'), 'utf8').then(JSON.parse)));
    const info = validatePackages(manifests, JSON.parse(await readFile(join(root, 'package-lock.json'), 'utf8')));
    const release = await findRelease(info.tag);
    const tags = run('git', ['tag', '--list', info.tag]);
    const tagCommit = tags ? run('git', ['rev-parse', `refs/tags/${info.tag}^{commit}`]) : null;
    const shouldPublish = validateRelease(release, info, sha, tagCommit);
    if (process.env.GITHUB_OUTPUT) await appendFile(process.env.GITHUB_OUTPUT, `publish=${shouldPublish}\n`);
    if (!shouldPublish) { console.log(`${info.tag} is already released; nothing to publish.`); return; }
    if (mode === 'check') { console.log(`Ready to validate ${info.tag} for npm tag ${info.distTag}.`); return; }
    if (!process.env.ACTIONS_ID_TOKEN_REQUEST_URL || !process.env.ACTIONS_ID_TOKEN_REQUEST_TOKEN || !process.env.npm_execpath) throw new Error('Publishing requires GitHub Actions OIDC and npm run release.');
    const directory = join(root, 'artifacts/packages');
    const packages = await verifyPackages(info, directory);
    await publishRelease({ info, sha, release, packages, directory, npm: process.env.npm_execpath, execute: run, lookup: name => json(`${registry}/${encodeURIComponent(name)}`) });
    if (process.env.GITHUB_STEP_SUMMARY) await appendFile(process.env.GITHUB_STEP_SUMMARY, `Published five packages at ${info.version} (npm tag: ${info.distTag}) and GitHub Release ${info.tag}.\n`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) await main();
