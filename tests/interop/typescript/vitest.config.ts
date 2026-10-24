import { defineConfig } from 'vitest/config';

export default defineConfig({
    test: {
        include: ['*.test.ts'],
        // Each workflow starts real .NET processes and persists multi-step SDK operations.
        testTimeout: 60_000,
        hookTimeout: 15_000,
        restoreMocks: true,
    },
});
