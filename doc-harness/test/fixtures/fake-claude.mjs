// 테스트용 가짜 claude CLI. stdin 프롬프트를 읽고 인자를 검사한 뒤 JSON 결과를 출력한다.
// FAKE_CLAUDE_MODE: ok(기본) | badjson | error | invalid_schema | invalid_then_ok
// FAKE_CLAUDE_STATE_FILE: 호출 횟수를 기록하는 파일(invalid_then_ok용)
import { readFileSync, writeFileSync, existsSync } from 'node:fs';

const args = process.argv.slice(2);
const prompt = readFileSync(0, 'utf8');
const mode = process.env.FAKE_CLAUDE_MODE ?? 'ok';
const stateFile = process.env.FAKE_CLAUDE_STATE_FILE;
let calls = 0;
if (stateFile) {
  calls = existsSync(stateFile) ? Number(readFileSync(stateFile, 'utf8')) : 0;
  writeFileSync(stateFile, String(calls + 1));
}

const schemaIdx = args.indexOf('--json-schema');
const schema = schemaIdx >= 0 ? JSON.parse(args[schemaIdx + 1]) : null;

function emit(structured, extra = {}) {
  process.stdout.write(JSON.stringify({
    structured_output: structured, result: JSON.stringify(structured), is_error: false, total_cost_usd: 0.01,
    session_id: 'fake-session', stop_reason: 'end_turn', num_turns: 2, fake: { args, promptEcho: prompt.slice(0, 200), promptSha: prompt.length }, ...extra,
  }));
}

if (mode === 'badjson') {
  process.stdout.write('this is not json');
} else if (mode === 'limit') {
  process.stdout.write(JSON.stringify({ is_error: true, result: "You've hit your session limit · resets 1am", total_cost_usd: 0, session_id: 'fake-limit' }));
} else if (mode === 'error') {
  process.stdout.write(JSON.stringify({ is_error: true, result: 'simulated failure', total_cost_usd: 0.002, session_id: 'fake-err' }));
} else if (mode === 'invalid_schema' || (mode === 'invalid_then_ok' && calls === 0)) {
  emit({ wrong: true });
} else {
  // 스키마의 required 키를 최소한으로 채운다(테스트 스키마는 단순 객체).
  const out = {};
  for (const key of schema?.required ?? []) {
    const p = schema.properties?.[key] ?? {};
    out[key] = p.type === 'boolean' ? true : p.type === 'array' ? [] : p.type === 'number' ? 1 : `echo:${key}`;
  }
  if (prompt.includes('retry-note-check') && !prompt.includes('스키마를 위반했다')) out.msg = 'no-retry-note';
  emit(out);
}
