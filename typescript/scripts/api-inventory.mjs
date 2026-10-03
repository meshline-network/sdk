import { readFile, stat, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';

const args = process.argv.slice(2);
if (args.length > 1 || args.length === 1 && args[0] !== '--check') throw new Error('Only --check is supported; omit it to regenerate the inventory.');
const checkOnly = args[0] === '--check';
const source = new URL('../../dotnet/docs/api/coverage.json', import.meta.url);
const bytes = await readFile(source);
const coverage = JSON.parse(bytes);
const mappings = JSON.parse(await readFile(new URL('./api/api-map.json', import.meta.url), 'utf8'));
const mappingTypes = new Set();
for (const mapping of mappings) {
    if (typeof mapping.type !== 'string' || !mapping.type || mappingTypes.has(mapping.type)) throw new Error(`Invalid or duplicate API mapping: ${mapping.type}`);
    mappingTypes.add(mapping.type);
    if (typeof mapping.path !== 'string' || !(await stat(new URL(`../${mapping.path}`, import.meta.url))).isFile()) throw new Error(`Missing API mapping destination: ${mapping.type}`);
    if (typeof mapping.notes !== 'string' || !mapping.notes.trim()) throw new Error(`API mapping requires adaptation notes: ${mapping.type}`);
}
const usedMappings = new Set();
const inventory = Object.entries(coverage.symbols).map(([symbol, documentation]) => {
    const mapping = mappings.find(value => symbol.slice(2).split('(')[0] === value.type || symbol.slice(2).startsWith(`${value.type}.`));
    if (mapping) usedMappings.add(mapping.type);
    return { symbol, documentation: `../../../dotnet/docs/api/${documentation}`, status: mapping ? 'mapped-requires-behavior-review' : 'unmapped',
        ...(mapping ? { typescript: `../../${mapping.path}`, notes: mapping.notes } : {}) };
});
for (const type of mappingTypes) if (!usedMappings.has(type)) throw new Error(`API mapping matches no current .NET documentation symbol: ${type}`);
const output = JSON.stringify({
    source: '../../../dotnet/docs/api/coverage.json', sourceSha256: createHash('sha256').update(bytes).digest('hex'),
    sdkBaseline: '18c7cc9a881c8603c3795b86940009e1c73d11a2',
    note: 'Symbol mappings are a worklist, not a feature-coverage or acceptance claim. Review overloads and behavioral requirements separately.',
    symbols: inventory,
}, null, 2) + '\n';
const destination = new URL('./api/api-inventory.json', import.meta.url);
if (checkOnly) {
    if (await readFile(destination, 'utf8') !== output) throw new Error('The API inventory is stale. Review .NET surface/mapping changes and run npm run inventory:api.');
} else await writeFile(destination, output);
console.log(`${checkOnly ? 'Verified' : 'Inventoried'} ${inventory.length} .NET documentation symbols; ${inventory.filter(value => value.status !== 'unmapped').length} have a TypeScript destination. Remaining symbols stay explicitly unmapped.`);
