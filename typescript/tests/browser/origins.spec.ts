import { readFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';
import type {} from './harness.js';

const { relay_origin: vectors } = JSON.parse(readFileSync(new URL('../../../tests/vectors/identity-auth-v1.json', import.meta.url), 'utf8')) as {
    relay_origin: { normalization_cases: { endpoint: string; expected_origin: string }[];
        comparison_cases: { left_endpoint: string; right_endpoint: string; expected_same_origin: boolean }[] };
};

test('portable IDNA preserves all independent relay-origin vectors in the browser bundle', async ({ page }) => {
    await page.goto('/tests/browser/'); await page.waitForFunction(() => Boolean(window.meshlineHarness));
    const result = await page.evaluate(rows => {
        const { relayOrigin } = window.meshlineHarness.sdk;
        return { normalized: rows.normalization_cases.map(row => relayOrigin(row.endpoint)),
            comparisons: rows.comparison_cases.map(row => relayOrigin(row.left_endpoint) === relayOrigin(row.right_endpoint)) };
    }, vectors);
    expect(result.normalized).toEqual(vectors.normalization_cases.map(row => row.expected_origin));
    expect(result.comparisons).toEqual(vectors.comparison_cases.map(row => row.expected_same_origin));
});
