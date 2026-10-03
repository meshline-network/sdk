import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { compareVersions, packageDirectories, publicationPlan, publishRelease, releaseVersion, validatePackages, validateRelease, verifyPackages } from '../../scripts/release.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));
const info = releaseVersion('0.1.0-alpha.2');
const sha = 'a'.repeat(40);
const hash = (algorithm, bytes, encoding = 'hex') => createHash(algorithm).update(bytes).digest(encoding);
async function fixture(t) {
    const base = join(root, 'artifacts/release-tests');
    await mkdir(base, { recursive: true });
    const directory = await mkdtemp(join(base, 'case-'));
    t.after(async () => {
        assert.equal(dirname(directory), base);
        await rm(directory, { recursive: true, force: true });
    });
    const packages = [];
    for (const name of packageDirectories.map(value => `@meshline/${value}`)) {
        const bytes = Buffer.from(`test archive for ${name}`);
        const filename = `${name.replace('@', '').replace('/', '-')}-${info.version}.tgz`;
        const entry = { name, version: info.version, filename, sha256: hash('sha256', bytes), integrity: `sha512-${hash('sha512', bytes, 'base64')}`, path: join(directory, filename) };
        await writeFile(entry.path, bytes);
        packages.push(entry);
    }
    await writeFile(join(directory, 'manifest.json'), JSON.stringify({ packages }));
    return { packages, directory };
}
const published = entry => ({ versions: { [info.version]: { dist: { integrity: entry.integrity } } }, 'dist-tags': { alpha: info.version } });

test('alpha, beta, rc and stable versions select distinct publication channels', () => {
    assert.deepEqual(info, { version: '0.1.0-alpha.2', tag: 'typescript-v0.1.0-alpha.2', distTag: 'alpha', prerelease: true });
    assert.equal(releaseVersion('1.0.0-beta.3').distTag, 'beta');
    assert.equal(releaseVersion('1.0.0-rc.1').distTag, 'rc');
    assert.equal(releaseVersion('1.0.0').distTag, 'latest');
    assert.equal(releaseVersion('1.0.0').prerelease, false);
    for (const value of ['v1.0.0', '01.0.0', '1.0.0-alpha.01', '1.0.0+build', '1.0.0-latest', '1.0.0-1', '1.0.0\n', undefined]) assert.throws(() => releaseVersion(value));
});

test('channel ordering follows numeric SemVer and cannot move backward', () => {
    for (const [a, b] of [['0.1.0-alpha.10', '0.1.0-alpha.2'], ['1.0.0', '1.0.0-rc.9'], ['1.0.0-beta', '1.0.0-alpha.99'], ['1.0.0-alpha.1', '1.0.0-alpha'], ['2.0.0', '1.99.99']]) {
        assert.equal(compareVersions(a, b), 1);
        assert.equal(compareVersions(b, a), -1);
        assert.equal(compareVersions(a, a), 0);
    }
});

test('all package versions, internal peers, lockfile and repository identity must agree', async () => {
    const manifests = await Promise.all(packageDirectories.map(directory => readFile(join(root, 'packages', directory, 'package.json'), 'utf8').then(JSON.parse)));
    const lock = JSON.parse(await readFile(join(root, 'package-lock.json'), 'utf8'));
    assert.equal(validatePackages(manifests, lock).version, manifests[0].version);
    for (const mutate of [
        values => values[1].version = '9.0.0',
        values => values[2].peerDependencies['@meshline/sdk'] = '*',
        values => values[3].repository.url = 'https://github.com/another/repo.git',
        values => values[4].publishConfig.registry = 'https://other.invalid',
    ]) {
        const changed = structuredClone(manifests); mutate(changed);
        assert.throws(() => validatePackages(changed, lock));
    }
    const stale = structuredClone(lock); stale.packages['packages/expo'].version = '9.0.0';
    assert.throws(() => validatePackages(manifests, stale), /package-lock/);
});

test('published releases skip later commits; drafts and tags must belong to the source commit', () => {
    assert.equal(validateRelease({ draft: false }, info, sha, 'b'.repeat(40)), false);
    assert.equal(validateRelease(null, info, sha, null), true);
    assert.equal(validateRelease({ draft: true, target_commitish: sha }, info, sha, sha), true);
    assert.throws(() => validateRelease({ draft: true, target_commitish: 'b'.repeat(40) }, info, sha, null), /another commit/);
    assert.throws(() => validateRelease(null, info, sha, 'b'.repeat(40)), /another commit/);
});

test('packed files are verified, complete and returned with core first', async t => {
    const { packages, directory } = await fixture(t);
    assert.deepEqual(await verifyPackages(info, directory), packages);
    await writeFile(packages[4].path, 'modified after validation');
    await assert.rejects(verifyPackages(info, directory), /checksum/);
});

test('missing, duplicate and path-traversing manifest entries are rejected', async t => {
    const { packages, directory } = await fixture(t);
    for (const entries of [packages.slice(1), [...packages.slice(0, 4), packages[0]], packages.map((entry, i) => i ? entry : { ...entry, filename: '../outside.tgz' })]) {
        await writeFile(join(directory, 'manifest.json'), JSON.stringify({ packages: entries }));
        await assert.rejects(verifyPackages(info, directory));
    }
});

test('a partial release resumes only with identical archives and the expected dist-tag', async t => {
    const { packages } = await fixture(t);
    const pending = await publicationPlan(packages, info, async name => name === packages[0].name ? published(packages[0]) : null);
    assert.deepEqual(pending, packages.slice(1));
    await assert.rejects(publicationPlan(packages, info, async () => ({ versions: { [info.version]: { dist: { integrity: 'different' } } } })), /different bytes/);
    await assert.rejects(publicationPlan(packages, info, async () => ({ 'dist-tags': { alpha: '0.1.0-alpha.10' } })), /backward/);
    await assert.rejects(publicationPlan(packages, info, async () => ({ versions: published(packages[0]).versions, 'dist-tags': {} })), /does not point/);
    await assert.rejects(publicationPlan(packages, info, async () => { throw new Error('HTTP 503'); }), /HTTP 503/);
});

test('a collision in the last package causes no external writes', async t => {
    const { packages, directory } = await fixture(t); const commands = [];
    await assert.rejects(publishRelease({ info, sha, packages, directory, npm: 'npm-cli.js', execute: (...args) => commands.push(args), lookup: async name => name === packages[4].name ? { versions: { [info.version]: { dist: { integrity: 'different' } } } } : null }), /different bytes/);
    assert.deepEqual(commands, []);
});

test('npm failure leaves a draft; retry publishes only missing packages before completing the release', async t => {
    const { packages, directory } = await fixture(t); const commands = []; const registry = new Map();
    let fail = true;
    const options = {
        info, sha, packages, directory, npm: 'npm-cli.js', lookup: async name => registry.get(name) ?? null,
        execute(command, args) {
            commands.push([command, args]);
            if (command === 'gh') return;
            const entry = packages.find(value => value.path === args[2]);
            assert.ok(entry); assert.ok(args.includes('--tag=alpha')); assert.ok(args.includes('--ignore-scripts')); assert.ok(args.includes('--provenance'));
            if (entry === packages[1] && fail) throw new Error('npm unavailable');
            registry.set(entry.name, published(entry));
        },
    };
    await assert.rejects(publishRelease(options), /npm unavailable/);
    assert.equal(commands[0][1][1], 'create');
    assert.equal(commands[1][1][1], 'upload');
    assert.ok(!commands.some(([, args]) => args.includes('--draft=false')));
    fail = false; commands.length = 0;
    await publishRelease({ ...options, release: { draft: true, target_commitish: sha } });
    assert.ok(!commands.some(([, args]) => args[1] === 'create'));
    assert.deepEqual(commands.filter(([command]) => command !== 'gh').map(([, args]) => args[2]), packages.slice(1).map(entry => entry.path));
    assert.ok(commands.at(-1)[1].includes('--draft=false'));
    assert.ok(commands.at(-1)[1].includes('--latest=false'));
    assert.equal((await readFile(join(directory, 'SHA256SUMS'), 'utf8')).split('\n').filter(Boolean).length, 5);
});

test('npm indexing delay is bounded and cannot mark an incomplete release public', async t => {
    const { packages, directory } = await fixture(t); const commands = []; let waits = 0;
    await assert.rejects(publishRelease({ info, sha, packages, directory, npm: 'npm-cli.js', execute: (...args) => commands.push(args), lookup: async () => null, wait: async () => { waits++; } }), /not indexed/);
    assert.equal(waits, 40);
    assert.equal(commands.filter(([command]) => command !== 'gh').length, 5);
    assert.ok(!commands.some(([, args]) => args.includes('--draft=false')));
});

test('all packages upload before waiting for scanning and become visible independently', async t => {
    const { packages, directory } = await fixture(t); const commands = []; let elapsed = 0;
    await publishRelease({
        info, sha, packages, directory, npm: 'npm-cli.js', execute: (...args) => commands.push(args),
        lookup: async name => {
            const index = packages.findIndex(entry => entry.name === name);
            return elapsed >= (index + 1) * 60_000 ? published(packages[index]) : null;
        },
        wait: async ms => {
            assert.equal(commands.filter(([command]) => command !== 'gh').length, 5);
            assert.ok(!commands.some(([, args]) => args.includes('--draft=false')));
            elapsed += ms;
        },
    });
    assert.equal(elapsed, 5 * 60_000);
    assert.ok(commands.at(-1)[1].includes('--draft=false'));
});
