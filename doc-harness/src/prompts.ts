import { readFileSync } from 'node:fs';
import path from 'node:path';
import { defaultHarnessDir } from './config.js';

const cache = new Map<string, string>();

export function readPrompt(name: string, promptsDir: string = path.join(defaultHarnessDir(), 'prompts')): string {
  const file = path.join(promptsDir, `${name}.md`);
  let text = cache.get(file);
  if (text === undefined) {
    text = readFileSync(file, 'utf8');
    cache.set(file, text);
  }
  return text;
}

/** `{{name}}`을 치환한다. 남는 변수가 있으면 프롬프트 결함이므로 throw. */
export function substitute(template: string, vars: Record<string, string>): string {
  const out = template.replace(/\{\{(\w+)\}\}/g, (_m, key: string) => {
    if (!(key in vars)) throw new Error(`프롬프트 변수가 비어 있다: {{${key}}}`);
    return vars[key];
  });
  return out;
}

/** 전역 규칙 + 단계 프롬프트(치환) + 선택적 꼬리(재시도 안내 등). */
export function buildPrompt(name: string, vars: Record<string, string>, opts: { promptsDir?: string; tail?: string } = {}): string {
  const rules = readPrompt('00_global_rules', opts.promptsDir);
  const body = substitute(readPrompt(name, opts.promptsDir), vars);
  return [rules.trim(), '', '---', '', body.trim(), opts.tail ? `\n${opts.tail.trim()}` : ''].join('\n');
}
