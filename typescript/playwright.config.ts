import { defineConfig } from '@playwright/test';

export default defineConfig({
    testDir: './tests/browser',
    testMatch: '**/*.spec.ts',
    timeout: 30_000,
    fullyParallel: true,
    workers: 3,
    use: { baseURL: 'http://127.0.0.1:4177', headless: true },
    webServer: { command: 'npm run serve:tests', url: 'http://127.0.0.1:4177/tests/browser/', reuseExistingServer: false },
    projects: [{ name: 'chromium', use: { browserName: 'chromium' } }, { name: 'firefox', use: { browserName: 'firefox' } }, { name: 'webkit', use: { browserName: 'webkit' } }],
});
