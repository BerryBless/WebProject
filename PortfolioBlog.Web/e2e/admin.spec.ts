import { expect, test, type Page } from '@playwright/test'
import { ADMIN_CSP } from '../admin-headers.ts'

// production 빌드 + 실제 보안 헤더 + 실제 백엔드. 단위 테스트가 볼 수 없는 것만 본다:
// 진짜 CodeMirror, 진짜 CSP, 진짜 쿠키·Origin 검사, 진짜 sandbox iframe.
const PASSWORD = process.env.E2E_ADMIN_PASSWORD!
// playwright.config.ts의 SPA_ORIGIN과 같은 값. 여기서 다시 import하지 않는 이유: 그 모듈은 로드 시 .e2e/env.json을
// 요구하고 부수효과(스크래치 첨부 디렉터리 생성)가 있다 — 이 스펙 파일은 config의 부수효과에 기대지 않는다.
const SPA_ORIGIN = 'https://localhost:4173'
// 1x1 PNG
const PNG = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64')

/** CSP 위반과 콘솔 오류를 모은다. 위반은 문서 이벤트로, 콘솔은 브라우저가 찍는 "Refused to…/blocked" 문구로 잡는다(iframe 안의 위반은 콘솔에만 나온다). */
async function watch(page: Page) {
  const problems: string[] = []
  page.on('console', message => {
    const text = message.text()
    // Chromium은 4xx 응답마다 "Failed to load resource"를 오류로 찍는다 — 이 시나리오의 409는 의도한 것이므로 뺀다.
    // 단, CSP가 리소스 로드 자체를 막았을 때도 같은 접두사로 찍힐 수 있어 CSP 문구가 섞인 줄은 거르지 않는다.
    if (/^Failed to load resource/.test(text) && !/CSP|Content Security/i.test(text)) return
    // Playwright의 addInitScript는 모든 프레임에 주입된다. sandbox="" 미리보기 프레임에서는 그 주입이 막히고 Chromium이 이 문구를 찍는다(실측) —
    // 앱의 스크립트가 아니라 이 테스트의 스크립트이며, 샌드박스가 동작한다는 증거다.
    if (/^Blocked script execution in 'about:srcdoc'/.test(text)) return
    if (message.type() === 'error' || /Content[- ]Security[- ]Policy|Refused to/i.test(text)) problems.push(`console: ${text.slice(0, 300)}`)
  })
  page.on('pageerror', error => problems.push(`pageerror: ${error.message}`))
  await page.addInitScript(() => document.addEventListener('securitypolicyviolation', e => {
    const w = window as unknown as { __csp?: string[] }
    ;(w.__csp ??= []).push(`${e.violatedDirective} ${e.blockedURI}`)
  }))
  return async () => [...problems, ...await page.evaluate(() => (window as unknown as { __csp?: string[] }).__csp ?? [])]
}

async function login(page: Page) {
  await page.getByLabel('비밀번호').fill(PASSWORD)
  await page.getByRole('button', { name: '로그인' }).click()
}

test('문서 응답에 배포될 보안 헤더가 붙는다', async ({ request }) => {
  const response = await request.get('/')
  const csp = response.headers()['content-security-policy']
  expect(csp).toContain("default-src 'none'")
  expect(csp).toContain("script-src 'self'")
  expect(csp).not.toContain("script-src 'self' 'unsafe")
  expect(csp).toContain("style-src-attr 'none'")
  expect(response.headers()['x-content-type-options']).toBe('nosniff')
  // 위 4개는 정본의 성질(뭐가 있고 뭐가 없어야 하는지)을 검사한다. 이 단언은 배포되는 값 자체가
  // admin-headers.ts의 ADMIN_CSP와 글자 그대로 같은지 본다 — 정본과 실제 응답이 갈라지면 여기서 걸린다.
  expect(csp).toBe(ADMIN_CSP)
})

test('세션이 없으면 API는 401이고, CSRF 헤더가 없으면 403이다(화면을 우회해도 서버가 막는다)', async ({ request }) => {
  expect((await request.get('/api/posts', { headers: { 'X-Requested-With': 'XMLHttpRequest' } })).status()).toBe(401)
  expect((await request.get('/api/posts')).status()).toBe(403)
})

// 오픈 리다이렉트 방지(src/lib/safeNext.ts)를 실제 브라우저 내비게이션으로 증명한다. 단위 테스트는 메모리 라우터(jsdom)로
// safeNext()의 반환값만 확인하므로, 그 값이 실제 브라우저에서 진짜로 교차 출처 이동을 일으키지 않는다는 것까지는 보이지 못한다.
// 로그인은 서버 세션을 만들므로, 앞선 변형이 남긴 세션으로 다음 변형이 "이미 로그인됨"으로 통과하지 않도록 변형마다 새 컨텍스트를 쓴다.
test('로그인 뒤 ?next=의 오픈 리다이렉트 변형은 전부 SPA 안에 남는다', async ({ browser }) => {
  const variants = [
    `/login?next=${encodeURIComponent('//evil.test/x')}`,
    '/.//evil.test',
    'https://evil.test/',
  ]
  for (const next of variants) {
    const context = await browser.newContext({ ignoreHTTPSErrors: true })
    const page = await context.newPage()
    try {
      const target = next.startsWith('/login?next=') ? next : `/login?next=${encodeURIComponent(next)}`
      await page.goto(target)
      await expect(page.getByRole('heading', { name: '관리자 로그인' })).toBeVisible()
      await login(page)
      await expect(page).toHaveURL(`${SPA_ORIGIN}/`)
      expect(new URL(page.url()).origin).toBe(SPA_ORIGIN)
    } finally {
      await context.close()
    }
  }
})

test('글쓰기 전 과정: 로그인 → 시리즈 → 새 글(편집기·이미지·미리보기) → 충돌 → 삭제 → 로그아웃', async ({ page, context, browser, browserName }) => {
  const problems = await watch(page)
  const slug = `e2e-${browserName}-${Date.now()}`

  // 로그인: 보호된 경로로 들어가면 로그인 화면을 거쳐 원래 경로로 돌아온다.
  await page.goto('/series')
  await expect(page.getByRole('heading', { name: '관리자 로그인' })).toBeVisible()
  await login(page)
  await expect(page.getByRole('heading', { name: '시리즈', exact: true })).toBeVisible()
  const session = (await context.cookies()).find(c => c.name === '__Host-AdminSession')
  expect(session).toMatchObject({ httpOnly: true, secure: true, sameSite: 'Strict', path: '/' })
  expect(await page.evaluate(() => document.cookie)).toBe('') // HttpOnly: 스크립트는 세션을 볼 수 없다

  // 시리즈 만들기
  await page.getByLabel(/^제목/).last().fill(`E2E 시리즈 ${browserName}`)
  await page.getByLabel(/^slug/).last().fill(`${slug}-series`)
  await page.getByRole('button', { name: '만들기' }).click()
  await expect(page.getByText(`/series/${slug}-series`)).toBeVisible()

  // 새 글
  await page.getByRole('link', { name: '글', exact: true }).click()
  await page.getByRole('link', { name: '새 글' }).click()
  await expect(page.getByText('저장하면 즉시 공개됩니다')).toBeVisible()
  await page.getByLabel(/^제목/).fill(`E2E 글 ${browserName}`)
  await page.getByLabel(/^slug/).fill(slug)
  await page.getByLabel('태그 추가').fill('E2E'); await page.keyboard.press('Enter')
  await page.getByLabel(/^시리즈/).selectOption({ label: `E2E 시리즈 ${browserName}` })
  const editor = page.getByLabel('본문(마크다운)')
  await editor.click()
  await page.keyboard.type('# 제목\n\n```csharp\nvar x = 1;\n```\n\n<script>window.__pwned = 1</script>\n\n')

  // 이미지 올리기 → 편집기에 마크다운이 들어간다
  await page.locator('input[type=file]').setInputFiles({ name: '그림 (1).png', mimeType: 'image/png', buffer: PNG })
  await expect(editor).toContainText('](/attachments/')

  // 미리보기: sandbox="" iframe 안에 공개 사이트 CSS·강조·이미지가 실제로 적용된다. 스크립트는 없다.
  const frameElement = page.getByTitle('미리보기')
  await expect(frameElement).toHaveAttribute('sandbox', '')
  const frame = page.frameLocator('iframe[title="미리보기"]')
  await expect(frame.locator('h1')).toHaveText('제목')
  await expect(frame.locator('pre span.keyword').first()).toHaveText('var')
  await expect(frame.locator('script')).toHaveCount(0)
  // 자식 프레임이 이 하나뿐이라는 전제: 소스 가드(source-guards.test.ts)가 iframe을 PreviewPane 한 곳으로 고정한다.
  const inFrame = page.frames().find(f => f !== page.mainFrame())!
  await expect.poll(() => inFrame.evaluate(() => {
    const img = document.querySelector('img')
    return { sheets: document.styleSheets.length, image: img ? img.naturalWidth : -1 }
  })).toEqual({ sheets: 2, image: 1 })
  expect(await page.evaluate(() => (window as unknown as { __pwned?: number }).__pwned)).toBeUndefined()

  // 저장 → 편집 주소로 바뀐다
  await page.getByRole('button', { name: '저장' }).click()
  await expect(page).toHaveURL(/\/posts\/[0-9a-f-]{36}$/)
  await expect(page.getByLabel(/^slug/)).toHaveAttribute('readonly', '')
  const postUrl = page.url()

  // 다른 탭에서 먼저 고친다 → 이 탭의 저장은 409 → 나란히 비교 → 내 내용으로 다시 저장
  const other = await (await browser.newContext({ ignoreHTTPSErrors: true, storageState: await context.storageState() })).newPage()
  await other.goto(postUrl)
  await other.getByLabel(/^제목/).fill(`다른 탭이 고친 제목 ${browserName}`)
  // 응답을 기다린다: 저장 버튼은 요청 중에도 disabled라서 "disabled가 됐다"로는 저장이 끝났는지 알 수 없다(실측: Chromium에서
  // 요청이 끝나기 전에 컨텍스트를 닫아 PUT이 취소됐고, 그래서 409가 나지 않았다).
  await Promise.all([
    other.waitForResponse(r => r.request().method() === 'PUT' && r.status() === 200),
    other.getByRole('button', { name: '저장' }).click(),
  ])
  await other.context().close()

  await page.getByLabel(/^요약/).fill('이 탭에서 쓴 요약')
  await page.getByRole('button', { name: '저장' }).click()
  const conflict = page.getByRole('alertdialog', { name: '저장 충돌' })
  await expect(conflict).toBeVisible()
  await conflict.getByRole('button', { name: /내 내용 유지/ }).click()
  await Promise.all([
    page.waitForResponse(r => r.request().method() === 'PUT' && r.status() === 200),
    page.getByRole('button', { name: '저장' }).click(),
  ])

  // 목록에서 찾고 지운다
  await page.getByRole('link', { name: '목록' }).click()
  await page.getByLabel('글 검색').fill(slug)
  const row = page.getByRole('row').filter({ hasText: `/posts/${slug}` })
  await expect(row).toBeVisible()
  page.once('dialog', dialog => void dialog.accept())
  await row.getByRole('button', { name: '삭제' }).click()
  await expect(row).toHaveCount(0)

  // 로그아웃 → 보호된 화면은 다시 로그인으로
  await page.getByRole('button', { name: '로그아웃' }).click()
  await expect(page.getByRole('heading', { name: '관리자 로그인' })).toBeVisible()

  expect(await problems()).toEqual([])
})
