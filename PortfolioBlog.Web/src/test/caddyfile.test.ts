import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'
import { ADMIN_SECURITY_HEADERS } from '../../admin-headers.ts'

// 운영에서 관리 SPA의 보안 헤더를 붙이는 것은 Caddy다(deploy/Caddyfile). 정본은 admin-headers.ts이고 Caddyfile은 그 사본이다.
// 이 테스트는 사본이 정본과 글자 그대로 같은지, 그리고 정본에 의도적으로 없는 HSTS가 Caddyfile에는 있는지 본다.
// 실제 응답의 헤더는 deploy/smoke(컨테이너 스택)가 본다 — 여기는 Docker 없이 매 커밋 도는 빠른 검사다.
const caddyfile = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..', 'deploy', 'Caddyfile'), 'utf8')

/** 관리 사이트 블록에서 `이름 "값"` 꼴의 헤더 줄을 모은다. 값에 큰따옴표가 들어가는 헤더는 없다(있으면 아래 개수 단언이 깨진다). */
function adminSiteHeaders(): Map<string, string> {
  const start = caddyfile.indexOf('{$ADMIN_DOMAIN} {')
  expect(start, '관리 사이트 블록').toBeGreaterThan(-1)
  const headers = new Map<string, string>()
  for (const line of caddyfile.slice(start).split(/\r?\n/)) {
    const match = /^\s*([A-Za-z][A-Za-z-]+) "([^"]*)"\s*$/.exec(line)
    if (match) headers.set(match[1]!, match[2]!)
  }
  return headers
}

describe('deploy/Caddyfile의 관리 사이트 헤더', () => {
  it('admin-headers.ts의 모든 헤더가 같은 값으로 들어 있다', () => {
    const headers = adminSiteHeaders()
    for (const [name, value] of Object.entries(ADMIN_SECURITY_HEADERS)) {
      expect(headers.get(name), name).toBe(value)
    }
  })

  it('정본에 없는 HSTS와 COOP를 Caddy가 더한다(백엔드의 HSTS와 같은 값)', () => {
    const headers = adminSiteHeaders()
    expect(ADMIN_SECURITY_HEADERS['Strict-Transport-Security']).toBeUndefined()
    expect(headers.get('Strict-Transport-Security')).toBe('max-age=31536000; includeSubDomains')
    expect(headers.get('Cross-Origin-Opener-Policy')).toBe('same-origin')
  })

  it('보안 헤더 블록은 백엔드 프록시보다 뒤에 있다(첨부의 sandbox CSP를 덮어쓰지 않는다)', () => {
    const admin = caddyfile.slice(caddyfile.indexOf('{$ADMIN_DOMAIN} {'))
    const proxy = admin.indexOf('reverse_proxy api:8080')
    const csp = admin.indexOf('Content-Security-Policy "')
    expect(proxy).toBeGreaterThan(-1)
    expect(csp).toBeGreaterThan(proxy)
    // 위치(순서) 단언만으로는 헤더 블록이 handle @backend 안(프록시 뒤, 탭 한 단계 더 깊이)으로 옮겨져도 통과해 버린다(csp > proxy는 여전히 참).
    // 중첩 깊이(caddy fmt 기준 route 레벨 = 탭 3개)까지 봐야 그 사보타주를 잡는다.
    expect(caddyfile).toMatch(/^\t\t\tContent-Security-Policy "/m)
    // 탭 깊이만으로는 헤더 블록이 route 안에서 file_server 뒤(정적 처리보다 나중)로 옮겨져도 통과해 버린다(깊이는 그대로라서).
    // 정적 처리(root * /srv)보다 앞이어야 file_server에 도달하기 전에 헤더가 적용된다 — 그 위치까지 조여야 한다(N-A).
    expect(csp).toBeLessThan(admin.indexOf('root * /srv'))
  })

  it('허용 IP 검사는 관리 사이트의 다른 어떤 처리보다 앞이고, 전부 route 블록 안에 있다', () => {
    const admin = caddyfile.slice(caddyfile.indexOf('{$ADMIN_DOMAIN} {'))
    const route = admin.indexOf('route {')
    const denied = admin.indexOf('respond @denied 404')
    expect(route).toBeGreaterThan(-1)
    expect(denied).toBeGreaterThan(route)
    for (const directive of ['reverse_proxy', 'file_server', 'try_files', 'root *']) {
      expect(admin.indexOf(directive), directive).toBeGreaterThan(denied)
    }
    expect(admin).toContain('@denied not remote_ip {$ADMIN_ALLOWED_CIDRS}')
  })

  it('백엔드로 가는 경로는 /api/*와 /attachments/*뿐이다(접두사 매칭이면 SPA의 /attachments 화면이 백엔드로 간다)', () => {
    expect(caddyfile).toContain('@backend path /api/* /attachments/*')
    expect(caddyfile).not.toMatch(/path [^\n]*\/attachments\*/)
  })
})
