import { access, readFile, readdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const files = [join(root, 'README.md'), join(root, 'TESTING.md'), join(root, '../README.md'), join(root, '../MAINTENANCE.md'),
    join(root, 'examples/README.md'), join(root, '../dotnet/docs/README.md')];
files.push(...['../tests/README.md', '../tests/scenarios/README.md', '../tests/interop/README.md'].map(path => join(root, path)));
async function collectMarkdown(directory) {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
        const path = join(directory, entry.name);
        if (entry.isDirectory()) await collectMarkdown(path);
        else if (entry.isFile() && entry.name.endsWith('.md')) files.push(path);
    }
}
await collectMarkdown(join(root, 'docs'));
for (const name of await readdir(join(root, 'packages'))) files.push(join(root, 'packages', name, 'README.md'));
let checked = 0;
for (const path of files) {
    const markdown = (await readFile(path, 'utf8')).replace(/^```[^\n]*\n[\s\S]*?^```/gm, '');
    for (const match of markdown.matchAll(/\[[^\]]*\]\(([^\s)]+)(?:\s+"[^"]*")?\)/g)) {
        const repositoryPrefix = 'https://github.com/meshline-network/sdk/blob/main/';
        const repositoryLink = match[1].startsWith(repositoryPrefix);
        const target = repositoryLink ? match[1].slice(repositoryPrefix.length) : match[1];
        if (/^[a-z][a-z0-9+.-]*:|^#/i.test(target)) continue;
        const local = decodeURIComponent(target.split('#')[0]);
        try { await access(resolve(repositoryLink ? join(root, '..') : dirname(path), local)); }
        catch (cause) { throw new Error(`Missing Markdown link in ${path}: ${target}`, { cause }); }
        checked++;
    }
}
console.log(`Verified ${checked} local/repository Markdown targets in ${files.length} documents (anchors and other external URLs are not checked).`);
