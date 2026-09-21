// E2E 준비물을 만든다: (1) 개발 인증서(.certs/), (2) 버려질 PostgreSQL 컨테이너, (3) 버려질 관리자 비밀번호의 해시(.e2e/env.json).
// 저장소에는 비밀번호도 해시도 남기지 않는다 — 실행할 때마다 새로 만든다. `node scripts/e2e-prepare.mjs certs`는 (1)만 한다.
import { execFileSync, spawnSync } from 'node:child_process'
import { randomBytes } from 'node:crypto'
import { existsSync, mkdirSync, writeFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const web = join(dirname(fileURLToPath(import.meta.url)), '..')
const api = process.env.BLOG_API_DIR ?? join(web, '..', 'PortfolioBlog.Api')
const only = process.argv[2]

function exportCerts() {
  const pem = join(web, '.certs', 'dev.pem')
  if (existsSync(pem) && existsSync(join(web, '.certs', 'dev.key'))) return
  mkdirSync(join(web, '.certs'), { recursive: true })
  // Windows·macOS에서는 이미 신뢰된 개발 인증서를 내보낸다. Linux CI에서는 신뢰되지 않은 인증서가 새로 만들어진다 —
  // Playwright(ignoreHTTPSErrors)와 Vite 프록시(secure: false)는 신뢰 여부를 보지 않으므로 그대로 쓴다.
  execFileSync('dotnet', ['dev-certs', 'https', '--export-path', pem, '--format', 'Pem', '--no-password'], { stdio: 'inherit' })
  if (!existsSync(join(web, '.certs', 'dev.key'))) throw new Error('dotnet dev-certs가 .certs/dev.key를 만들지 않았습니다.')
}

function ensurePostgres(port, password) {
  if (process.env.E2E_SKIP_DOCKER === '1') return // CI: 워크플로의 services.postgres를 쓴다
  const name = 'pb-e2e-pg'
  spawnSync('docker', ['rm', '-f', name], { stdio: 'ignore' }) // 앞 실행의 데이터로 시작하지 않는다
  execFileSync('docker', ['run', '-d', '--name', name, '-e', `POSTGRES_PASSWORD=${password}`, '-e', 'POSTGRES_DB=blog_e2e',
    '-p', `127.0.0.1:${port}:5432`, 'postgres:17-alpine'], { stdio: 'inherit' })
  for (let i = 0; i < 60; i++) {
    if (spawnSync('docker', ['exec', name, 'pg_isready', '-U', 'postgres', '-d', 'blog_e2e'], { stdio: 'ignore' }).status === 0) return
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 500)
  }
  throw new Error('PostgreSQL 컨테이너가 준비되지 않았습니다.')
}

exportCerts()
if (only !== 'certs') {
  const port = process.env.E2E_PG_PORT ?? '5433'
  const pgPassword = process.env.E2E_PG_PASSWORD ?? randomBytes(12).toString('hex')
  ensurePostgres(port, pgPassword)
  const adminPassword = randomBytes(18).toString('base64url')
  // hash-password는 표준 입력으로 비밀번호를 받고 마지막 줄에 해시를 쓴다(실측).
  const output = execFileSync('dotnet', ['run', '--project', api, '-c', 'Release', '--', 'hash-password'], { input: adminPassword + '\n', encoding: 'utf8' })
  const hash = output.trim().split(/\r?\n/).at(-1)
  if (!hash || hash.length < 40) throw new Error('hash-password의 출력을 해석하지 못했습니다.')
  mkdirSync(join(web, '.e2e'), { recursive: true })
  writeFileSync(join(web, '.e2e', 'env.json'), JSON.stringify({ port, pgPassword, adminPassword, hash }), { mode: 0o600 })
  console.log('e2e 준비 완료')
}
