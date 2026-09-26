// E2E 준비물을 만든다: (1) 개발 인증서(.certs/), (2) 버려질 MySQL 컨테이너, (3) 버려질 관리자 비밀번호의 해시(.e2e/env.json).
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

function ensureMySql(port, password) {
  if (process.env.E2E_SKIP_DOCKER === '1') return // CI: 워크플로의 services.mysql을 쓴다
  const name = 'pb-e2e-mysql'
  spawnSync('docker', ['rm', '-f', name], { stdio: 'ignore' }) // 앞 실행의 데이터로 시작하지 않는다
  execFileSync('docker', ['run', '-d', '--name', name, '-e', `MYSQL_ROOT_PASSWORD=${password}`, '-e', 'MYSQL_DATABASE=blog_e2e',
    '-p', `127.0.0.1:${port}:3306`, 'mysql:8.4', '--transaction-isolation=READ-COMMITTED', '--character-set-server=utf8mb4', '--local-infile=0'], { stdio: 'inherit' })
  // TCP(-h 127.0.0.1)로 실제 쿼리를 한다: init 중 임시 서버는 네트워크를 닫아 두므로 이것이 성공하면 init이 끝난 것이다.
  for (let i = 0; i < 120; i++) {
    if (spawnSync('docker', ['exec', '-e', `MYSQL_PWD=${password}`, name, 'mysql', '-h', '127.0.0.1', '-uroot', '-N', '-e', 'SELECT 1', 'blog_e2e'], { stdio: 'ignore' }).status === 0) return
    Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 500)
  }
  throw new Error('MySQL 컨테이너가 준비되지 않았습니다.')
}

exportCerts()
if (only !== 'certs') {
  const port = process.env.E2E_DB_PORT ?? '3307'
  const dbPassword = process.env.E2E_DB_PASSWORD ?? randomBytes(12).toString('hex')
  ensureMySql(port, dbPassword)
  const adminPassword = randomBytes(18).toString('base64url')
  // hash-password는 표준 입력으로 비밀번호를 받고 마지막 줄에 해시를 쓴다(실측).
  const output = execFileSync('dotnet', ['run', '--project', api, '-c', 'Release', '--', 'hash-password'], { input: adminPassword + '\n', encoding: 'utf8' })
  const hash = output.trim().split(/\r?\n/).at(-1)
  if (!hash || hash.length < 40) throw new Error('hash-password의 출력을 해석하지 못했습니다.')
  mkdirSync(join(web, '.e2e'), { recursive: true })
  writeFileSync(join(web, '.e2e', 'env.json'), JSON.stringify({ port, dbPassword, adminPassword, hash }), { mode: 0o600 })
  console.log('e2e 준비 완료')
}
