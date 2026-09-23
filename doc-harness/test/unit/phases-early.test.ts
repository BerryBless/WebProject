import { existsSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { FakeClaudeRunner } from '../../src/claude.js';
import { runArchitecture } from '../../src/phases/architecture.js';
import { runDiscovery } from '../../src/phases/discovery.js';
import { runInventory } from '../../src/phases/inventory.js';
import { makeTestContext } from '../helpers/context.js';
import { sampleArchitecture, sampleFeatures, sampleInventory } from '../helpers/samples.js';

describe('phases 01~03', () => {
  it('inventory는 파일 트리·매니페스트 경로를 주입하고 결과를 staging에 쓴다', async () => {
    const fake = new FakeClaudeRunner({ inventory: sampleInventory() });
    const { ctx } = await makeTestContext(fake);
    const inv = await runInventory(ctx);
    expect(inv.project.name).toBe('Mini');
    expect(fake.calls[0].prompt).toContain('Api/Features/Posts/');
    expect(fake.calls[0].prompt).toContain('- Mini.Api.csproj');
    expect(fake.calls[0].prompt).not.toContain('changeme');
    expect(fake.calls[0].schemaName).toBe('inventory');
    expect(existsSync(path.join(ctx.run.staging.current, 'inventory.json'))).toBe(true);
    expect(ctx.run.item('inventory')).toBe('SUCCESS');
  });

  it('architecture는 진입점 파일과 이전 분석·변경 힌트를 주입한다', async () => {
    const fake = new FakeClaudeRunner({ architecture: sampleArchitecture() });
    const { ctx } = await makeTestContext(fake);
    const arch = await runArchitecture(ctx, sampleInventory(), null, null);
    expect(arch.components).toHaveLength(3);
    const p = fake.calls[0].prompt;
    expect(p).toContain('- Api/Program.cs');
    expect(p).toContain('최초 분석');
    expect(p).toContain('변경 힌트 없음');
    expect(fake.calls[0].phase).toBe('architecture');
  });

  it('discovery는 컴포넌트와 정답 목록을 주입하고 id 순으로 정렬한다', async () => {
    const shuffled = sampleFeatures();
    shuffled.features.reverse();
    const fake = new FakeClaudeRunner({ discovery: shuffled });
    const { ctx } = await makeTestContext(fake);
    const feats = await runDiscovery(ctx, sampleInventory(), sampleArchitecture());
    expect(feats.features.map((f) => f.id)).toEqual(['F001', 'F002']);
    const p = fake.calls[0].prompt;
    expect(p).toContain('POST /api/auth/login');
    expect(p).toContain('"id": "AppDbContext"');
    expect(p).toContain('상한 30개');
  });

  it('실패한 호출은 항목 FAILED로 남고 예외가 난다', async () => {
    const fake = new FakeClaudeRunner({ inventory: new Error('quota') });
    const { ctx } = await makeTestContext(fake);
    await expect(runInventory(ctx)).rejects.toThrow('quota');
    expect(ctx.run.item('inventory')).toBe('FAILED');
  });
});
