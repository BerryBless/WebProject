import { readdirSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { Ajv2020, type ValidateFunction } from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';
import { defaultHarnessDir } from './config.js';

export type SchemaName =
  | 'inventory' | 'architecture' | 'features' | 'feature' | 'data' | 'api' | 'failures' | 'operations_area' | 'operations'
  | 'verification' | 'consistency' | 'classification' | 'feature_delta' | 'diagram_update' | 'document' | 'failure_delta';

type JsonSchema = Record<string, unknown>;

const schemaDir = path.join(defaultHarnessDir(), 'schemas');
let commonDefs: Record<string, JsonSchema> | null = null;
const inlinedCache = new Map<string, JsonSchema>();
const validatorCache = new Map<string, ValidateFunction>();

function loadCommonDefs(): Record<string, JsonSchema> {
  if (!commonDefs) {
    const common = JSON.parse(readFileSync(path.join(schemaDir, 'common.schema.json'), 'utf8')) as { $defs: Record<string, JsonSchema> };
    commonDefs = common.$defs;
  }
  return commonDefs;
}

/**
 * `common.schema.json#/$defs/x` 참조를 전부 인라인한다.
 * claude CLI의 --json-schema는 외부 $ref를 해석하지 않으므로 자기 완결 스키마가 필요하다.
 * $defs 안의 재귀 참조가 없다는 전제(현재 공통 정의는 비재귀)로 깊이 우선 치환한다.
 */
export function inlineDefs(node: unknown, defs: Record<string, JsonSchema> = loadCommonDefs(), depth = 0): unknown {
  if (depth > 40) throw new Error('스키마 $ref 치환이 너무 깊다(순환 참조?)');
  if (Array.isArray(node)) return node.map((n) => inlineDefs(n, defs, depth + 1));
  if (node && typeof node === 'object') {
    const obj = node as Record<string, unknown>;
    if (typeof obj.$ref === 'string') {
      const m = /^common\.schema\.json#\/\$defs\/(\w+)$/.exec(obj.$ref);
      if (!m) throw new Error(`지원하지 않는 $ref: ${obj.$ref}`);
      const target = defs[m[1]];
      if (!target) throw new Error(`common $defs에 없는 정의: ${m[1]}`);
      const { $ref: _ref, ...rest } = obj;
      return { ...(inlineDefs(target, defs, depth + 1) as JsonSchema), ...(inlineDefs(rest, defs, depth + 1) as JsonSchema) };
    }
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(obj)) {
      if (k === '$id') continue;
      out[k] = inlineDefs(v, defs, depth + 1);
    }
    return out;
  }
  return node;
}

/** `--json-schema`에 그대로 넘길 수 있는 자기 완결 스키마. */
export function loadSchema(name: SchemaName): JsonSchema {
  const cached = inlinedCache.get(name);
  if (cached) return cached;
  const raw = JSON.parse(readFileSync(path.join(schemaDir, `${name}.schema.json`), 'utf8')) as JsonSchema;
  const inlined = inlineDefs(raw) as JsonSchema;
  inlinedCache.set(name, inlined);
  return inlined;
}

export function listSchemaNames(): string[] {
  return readdirSync(schemaDir).filter((f) => f.endsWith('.schema.json') && f !== 'common.schema.json').map((f) => f.replace('.schema.json', ''));
}

function validator(name: SchemaName): ValidateFunction {
  let v = validatorCache.get(name);
  if (!v) {
    const ajv = new Ajv2020({ allErrors: true, strict: false });
    addFormats.default ? addFormats.default(ajv) : (addFormats as unknown as (a: Ajv2020) => void)(ajv);
    v = ajv.compile(loadSchema(name));
    validatorCache.set(name, v);
  }
  return v;
}

export function validate(name: SchemaName, data: unknown): { ok: true } | { ok: false; errors: string[] } {
  const v = validator(name);
  if (v(data)) return { ok: true };
  const errors = (v.errors ?? []).map((e) => `${e.instancePath || '/'} ${e.message ?? ''}${e.params && 'additionalProperty' in e.params ? ` (${String(e.params.additionalProperty)})` : ''}`.trim());
  return { ok: false, errors };
}
