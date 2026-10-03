/** Both Expo SQLite and FileSystem consume file URIs on Android and iOS. */
export function databaseLocation(directory: string, name: string): { directoryUri: string; encodedName: string; fileUri: string } {
    if (!name.trim() || name === '.' || name === '..' || /[\x00-\x1f\x7f/\\:?]/.test(name)) throw new TypeError('Expected a persistent SQLite database basename.');
    if (typeof directory !== 'string' || directory.length === 0) throw new Error('A native SQLite database directory is required.');
    let path = directory;
    if (directory.startsWith('file:///')) {
        if (/[?#]/.test(directory)) throw new TypeError('A database directory URI cannot contain a query or fragment.');
        path = decodeURIComponent(directory.slice('file://'.length));
    }
    if (!path.startsWith('/') || /[\x00-\x1f\x7f\\]/.test(path)) throw new TypeError('Expected an absolute local database directory or file URI.');
    // Android's defaultDatabaseDirectory is an absolute path without a URI scheme.
    // Escape each component once so #, %, spaces and Unicode name the same file
    // in the existence probe and in SQLite's native URI-to-path conversion.
    const directoryUri = `file://${path.split('/').map(encodeURIComponent).join('/').replace(/\/+$/, '')}`;
    const encodedName = encodeURIComponent(name);
    return { directoryUri, encodedName, fileUri: `${directoryUri}/${encodedName}` };
}
