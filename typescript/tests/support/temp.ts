import { realpath, rm } from 'node:fs/promises';
import { basename, dirname } from 'node:path';
import { tmpdir } from 'node:os';

/** Restricts recursive test cleanup to a resolved, directly owned Meshline temp directory. */
export async function removeTestDirectory(path: string): Promise<void> {
    const root = await realpath(tmpdir());
    const target = await realpath(path);
    if (dirname(target).toLowerCase() !== root.toLowerCase() || !basename(target).startsWith('meshline-'))
        throw new Error(`Refusing to remove a non-test directory: ${target}`);
    await rm(target, { recursive: true, force: true });
}
