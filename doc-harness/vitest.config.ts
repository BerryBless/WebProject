import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    include: ['test/unit/**/*.test.ts', ...(process.env.DOC_HARNESS_LIVE ? ['test/integration/**/*.live.test.ts'] : [])],
    testTimeout: 60_000,
    hookTimeout: 60_000,
  },
});
