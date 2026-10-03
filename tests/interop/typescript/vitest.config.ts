import { defineConfig } from 'vitest/config';

export default defineConfig({
    test: {
        include: ['*.test.ts'],
        testTimeout: 15_000,
        hookTimeout: 15_000,
        restoreMocks: true,
    },
});
