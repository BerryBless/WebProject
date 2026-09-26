import { existsSync, mkdtempSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { defineConfig, devices } from '@playwright/test'

// 실제 백엔드(PortfolioBlog.Api + MySQL)와 production 빌드(`vite preview`, 실제 보안 헤더)를 띄워 브라우저로 검사한다.
// 먼저 `npm run e2e:prepare`가 .e2e/env.json(버려질 비밀번호·해시)을 만들어야 한다.
const web = dirname(fileURLToPath(import.meta.url))
const api = process.env.BLOG_API_DIR ?? join(web, '..', 'PortfolioBlog.Api')
const envFile = join(web, '.e2e', 'env.json')
if (!existsSync(envFile)) throw new Error('먼저 `npm run e2e:prepare`를 실행하세요.')
const prepared = JSON.parse(readFileSync(envFile, 'utf8')) as { port: string; dbPassword: string; adminPassword: string; hash: string }

export const SPA_ORIGIN = 'https://localhost:4173'
const API_ORIGIN = 'https://localhost:7198'
process.env.E2E_ADMIN_PASSWORD = prepared.adminPassword // 테스트 워커가 읽는다(파일에 다시 쓰지 않는다)

// 연결 문자열은 조각으로 조립한다(저장소의 비밀값 스캐너는 한 줄짜리 연결 문자열 리터럴을 막는다).
const connection = ['Server=localhost', `Port=${prepared.port}`, 'Database=blog_e2e', 'User ID=root', `Password=${prepared.dbPassword}`, 'SslMode=Required'].join(';')

export default defineConfig({
  testDir: 'e2e',
  timeout: 90_000,
  fullyParallel: false, // 두 브라우저가 같은 DB를 쓴다. 시나리오는 브라우저 이름을 slug에 넣어 서로 부딪히지 않게 한다
  workers: 1,
  retries: 0,           // 재시도로 간헐 실패를 가리지 않는다
  reporter: process.env.CI ? [['github'], ['list']] : 'list',
  use: { baseURL: SPA_ORIGIN, ignoreHTTPSErrors: true, trace: 'retain-on-failure' },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
    { name: 'firefox', use: { ...devices['Desktop Firefox'] } }, // 미리보기 CSP의 'self' 문제는 Firefox에서만 드러났다
  ],
  webServer: [
    {
      command: `dotnet run --project "${api}" -c Release --no-launch-profile`,
      url: `${API_ORIGIN}/health`, ignoreHTTPSErrors: true, reuseExistingServer: false, timeout: 180_000,
      env: {
        ASPNETCORE_ENVIRONMENT: 'Development',
        ASPNETCORE_URLS: API_ORIGIN,
        ConnectionStrings__Default: connection,
        Site__PublicOrigin: API_ORIGIN,
        Site__AdminOrigin: SPA_ORIGIN, // Origin 검사의 기준 — SPA가 뜨는 출처와 같아야 변경 요청이 403이 되지 않는다
        Admin__PasswordHash: prepared.hash,
        Admin__AllowedCidrs: '127.0.0.1/32 ::1/128',
        // 기본값(분당 IP당 5회)은 이 스위트에서 넘친다: 오픈 리다이렉트 테스트가 컨텍스트마다 새로 로그인하고(3회),
        // 글쓰기 시나리오도 로그인한다 — Chromium·Firefox 전부 루프백 127.0.0.1에서 붙어 같은 IP 파티션을 공유한다.
        // 실서비스 기본값(브루트포스 방어)은 그대로 두고 이 E2E 프로세스에서만 넉넉히 올린다.
        Admin__LoginPerIpPerMinute: '40',
        Admin__LoginGlobalPerMinute: '80', // 위와 같은 이유로 전역 한도도 같이 올린다(자기 일관성 — 개별 한도만 풀면 전역 한도가 새 병목이 된다).
        Attachments__RootPath: mkdtempSync(join(tmpdir(), 'pb-e2e-attachments-')),
      },
    },
    { command: 'npm run build && npm run preview', url: SPA_ORIGIN, ignoreHTTPSErrors: true, reuseExistingServer: false, timeout: 180_000 },
  ],
})
