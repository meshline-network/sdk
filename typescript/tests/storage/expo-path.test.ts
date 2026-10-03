import { fileURLToPath } from 'node:url';
import { expect, test } from 'vitest';
import { databaseLocation } from '../../packages/expo/src/database-path.js';

test.each(['/data/user/0/app/files/SQLite', 'file:///data/user/0/app/files/SQLite/'])('native file probe and SQLite open identify the same file for %s', directory => {
    const location = databaseLocation(directory, 'state #中文%.sqlite');
    const path = '/data/user/0/app/files/SQLite/state #中文%.sqlite';
    expect(fileURLToPath(location.fileUri, { windows: false })).toBe(path);
    expect(fileURLToPath(`${location.directoryUri}/${location.encodedName}`, { windows: false })).toBe(path);
});

test('absolute paths and escaped URI directories preserve literal percent and fragment characters', () => {
    const fromPath = databaseLocation('/data/user/0/app/files/100% #中文', 'state.sqlite');
    const fromUri = databaseLocation('file:///data/user/0/app/files/100%25%20%23%E4%B8%AD%E6%96%87', 'state.sqlite');
    expect(fromPath).toEqual(fromUri);
    expect(fileURLToPath(fromPath.fileUri, { windows: false })).toBe('/data/user/0/app/files/100% #中文/state.sqlite');
});

test.each(['relative/path', 'https://example.test/state', 'content://documents/state', 'file://remote/state', 'file:///state?query', 'file:///state#fragment', 'file:///state%zz'])('rejects unsupported or ambiguous database directory %s', directory => {
    expect(() => databaseLocation(directory, 'state.sqlite')).toThrow();
});
