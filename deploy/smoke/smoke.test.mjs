// 배포 스택(Caddy + api + postgres) 스모크 테스트. compose 네트워크 안의 컨테이너에서 돈다(docker-compose.smoke.yml).
// ROLE=allowed: 관리 허용 목록 안의 IP(172.30.0.10). ROLE=denied: 밖의 IP(172.30.0.11).
// fetch를 쓰지 않는다: Host·경로를 정규화 없이 그대로 보내야 하는 검사가 있다(node:http는 경로를 손대지 않는다).
import assert from 'node:assert/strict'
import http from 'node:http'
import https from 'node:https'
import net from 'node:net'
import { test } from 'node:test'
import zlib from 'node:zlib'
import { createHash } from 'node:crypto'
import { ADMIN_SECURITY_HEADERS } from '/web/admin-headers.ts'

const { ROLE, DOMAIN, ADMIN_DOMAIN, PUBLIC_ORIGIN, ADMIN_ORIGIN, SMOKE_ADMIN_PASSWORD } = process.env
for (const [key, value] of Object.entries({ ROLE, DOMAIN, ADMIN_DOMAIN, PUBLIC_ORIGIN, ADMIN_ORIGIN, SMOKE_ADMIN_PASSWORD })) {
  assert.ok(value, `환경변수 ${key}가 필요합니다`)
}
const XRW = { 'X-Requested-With': 'XMLHttpRequest' }
const PUBLIC_CSP = "default-src 'none'; img-src 'self'; style-src 'self'; font-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'"
const HSTS = 'max-age=31536000; includeSubDomains'
const MIB = 1024 * 1024

/** 요청 하나를 보내고 상태·헤더·본문을 돌려준다. path는 정규화 없이 그대로 나간다. */
function send({ host, path = '/', method = 'GET', headers = {}, body, chunks, tls = true, port, servername }) {
  return new Promise((resolve, reject) => {
    const options = { host, port: port ?? (tls ? 443 : 80), path, method, headers: { Host: host, ...headers }, servername: servername ?? host }
    const request = (tls ? https : http).request(options, response => {
      const parts = []
      response.on('data', part => parts.push(part))
      response.on('end', () => resolve({ status: response.statusCode, headers: response.headers, body: Buffer.concat(parts) }))
      response.on('error', reject)
    })
    request.on('error', reject)
    request.setTimeout(60_000, () => request.destroy(new Error(`시간 초과: ${method} ${host}${path}`)))
    if (chunks) { for (const part of chunks) request.write(part); request.end() } // Content-Length 없이 → chunked
    else request.end(body)
  })
}

const pub = (path, extra = {}) => send({ host: DOMAIN, path, ...extra })
const adm = (path, extra = {}) => send({ host: ADMIN_DOMAIN, path, ...extra })

function pngChunk(type, data) {
  const head = Buffer.alloc(4); head.writeUInt32BE(data.length)
  const typed = Buffer.concat([Buffer.from(type, 'latin1'), data])
  const crc = Buffer.alloc(4); crc.writeUInt32BE(zlib.crc32(typed) >>> 0)
  return Buffer.concat([head, typed, crc])
}

/** 대략 approxBytes 크기의 유효한 PNG. 픽셀이 난수라 압축되지 않는다(크기 경계 검사용). seed가 다르면 내용(sha)도 다르다. */
function makePng(approxBytes, seed) {
  const width = 256, rowBytes = 1 + width * 3, rows = Math.max(1, Math.floor(approxBytes / rowBytes))
  const raw = Buffer.alloc(rows * rowBytes)
  let state = seed >>> 0
  for (let i = 0; i < raw.length; i++) { state = (Math.imul(state, 1664525) + 1013904223) >>> 0; raw[i] = i % rowBytes === 0 ? 0 : state >>> 24 }
  const header = Buffer.alloc(13); header.writeUInt32BE(width, 0); header.writeUInt32BE(rows, 4); header.set([8, 2, 0, 0, 0], 8)
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), pngChunk('IHDR', header),
    pngChunk('IDAT', zlib.deflateSync(raw, { level: 0 })), pngChunk('IEND', Buffer.alloc(0))])
}

function multipart(fileName, bytes) {
  const boundary = `----smoke${Date.now().toString(16)}`
  const head = Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="${fileName}"\r\nContent-Type: image/png\r\n\r\n`)
  const tail = Buffer.from(`\r\n--${boundary}--\r\n`)
  return { contentType: `multipart/form-data; boundary=${boundary}`, body: Buffer.concat([head, bytes, tail]) }
}

function assertEmpty404(response, label) {
  assert.equal(response.status, 404, label)
  assert.equal(response.body.length, 0, `${label}: Caddy의 404는 본문이 없다(본문이 있으면 백엔드까지 닿은 것이다)`)
  assert.equal(response.headers['content-security-policy'], undefined, `${label}: 백엔드 응답이 아니어야 한다`)
}

function assertNoProductHeaders(response, label) {
  assert.equal(response.headers.server, undefined, `${label}: Server 헤더`)
  assert.equal(response.headers.via, undefined, `${label}: Via 헤더`)
}

if (ROLE === 'allowed') {
  test('공개 사이트: 페이지·헬스가 뜨고 백엔드의 보안 헤더가 그대로 온다', async () => {
    const health = await pub('/health')
    assert.equal(health.status, 200)
    const home = await pub('/')
    assert.equal(home.status, 200)
    assert.equal(home.headers['content-security-policy'], PUBLIC_CSP)
    assert.equal(home.headers['strict-transport-security'], HSTS)
    assert.equal(home.headers['x-content-type-options'], 'nosniff')
    assert.equal(home.headers['x-frame-options'], 'DENY')
    assertNoProductHeaders(home, '공개 /')
    const css = await pub('/css/site.css')
    assert.equal(css.status, 200)
    for (const path of ['/css/site.css.gz', '/css/site.css.br', '/web.config', '/appsettings.json']) {
      assert.equal((await pub(path)).status, 404, path)
    }
  })

  test('공개 사이트: /api는 어떤 표기로도 Caddy가 404로 끊는다', async () => {
    for (const path of ['/api', '/api/', '/api/posts', '/API/posts', '/Api/auth/login', '/%61pi/posts', '/api%2fposts', '//api/posts', '/x/../api/posts', '/./api/posts']) {
      for (const method of ['GET', 'POST']) {
        const response = await pub(path, { method, headers: XRW })
        assert.equal(response.status, 404, `${method} ${path}`)
        assertNoProductHeaders(response, `${method} ${path}`)
      }
    }
    assertEmpty404(await pub('/api/posts', { headers: XRW }), '/api/posts')
    assertEmpty404(await pub('/API/posts', { headers: XRW }), '/API/posts')
  })

  test('공개 사이트: 평문 HTTP는 HTTPS로 넘긴다', async () => {
    const response = await pub('/', { tls: false })
    assert.equal(response.status, 308)
    assert.match(response.headers.location, new RegExp(`^https://${DOMAIN.replaceAll('.', '\\.')}/`))
  })

  test('공개 사이트: 본문이 큰 요청과 긴 요청 줄은 5xx 없이 거부된다', async () => {
    const big = await pub('/', { method: 'POST', body: Buffer.alloc(100 * 1024, 0x61), headers: { 'Content-Type': 'text/plain' } })
    assert.equal(big.status, 413)
    const long = await pub(`/search?q=${'a'.repeat(9000)}`)
    assert.ok([414, 431].includes(long.status), `긴 요청 줄: ${long.status}`)
  })

  test('관리 사이트: 정적 SPA에 admin-headers.ts의 값 + HSTS가 붙는다', async () => {
    const home = await adm('/')
    assert.equal(home.status, 200)
    assert.match(home.headers['content-type'], /^text\/html/)
    for (const [name, value] of Object.entries(ADMIN_SECURITY_HEADERS)) {
      assert.equal(home.headers[name.toLowerCase()], value, name)
    }
    assert.equal(home.headers['strict-transport-security'], HSTS)
    assert.equal(home.headers['cross-origin-opener-policy'], 'same-origin')
    assert.equal(home.headers['cache-control'], 'no-cache')
    assertNoProductHeaders(home, '관리 /')
  })

  test('관리 사이트: SPA 화면 주소는 index.html, /assets의 없는 파일은 404', async () => {
    const index = (await adm('/')).body.toString('utf8')
    for (const path of ['/attachments', '/posts/new', '/series', '/login?next=%2Ftags']) {
      const response = await adm(path)
      assert.equal(response.status, 200, path)
      assert.equal(response.body.toString('utf8'), index, `${path}는 SPA 문서여야 한다(백엔드로 가면 안 된다)`)
    }
    const script = /src="(\/assets\/[^"]+\.js)"/.exec(index)?.[1]
    assert.ok(script, 'index.html에 /assets 스크립트가 있어야 한다')
    const hit = await adm(script, { headers: { 'Accept-Encoding': 'gzip' } })
    assert.equal(hit.status, 200)
    assert.equal(hit.headers['cache-control'], 'public, max-age=31536000, immutable')
    assert.equal(hit.headers['content-encoding'], 'gzip')
    assert.equal(hit.headers['content-security-policy'], ADMIN_SECURITY_HEADERS['Content-Security-Policy'])
    const miss = await adm('/assets/missing-0000.js')
    assert.equal(miss.status, 404)
    assert.equal(miss.headers['cache-control'], undefined, '404를 영구 캐시하게 두면 안 된다')
    assertNoProductHeaders(miss, '/assets 404')
  })

  test('관리 API: CSRF 헤더 검사와 백엔드 헤더가 Caddy를 지나도 그대로다', async () => {
    assert.equal((await adm('/api/auth/me')).status, 403)
    const me = await adm('/api/auth/me', { headers: XRW })
    assert.equal(me.status, 200)
    assert.deepEqual(JSON.parse(me.body.toString('utf8')), { authenticated: false })
    assert.equal(me.headers['content-security-policy'], PUBLIC_CSP, 'API 응답의 CSP는 백엔드 값이다(SPA용 CSP로 덮이면 안 된다)')
    assert.equal(me.headers['cache-control'], 'no-store')
    assertNoProductHeaders(me, '/api/auth/me')
    assert.equal((await adm('/api/posts', { headers: XRW })).status, 401)
  })

  test('글쓰기 전 과정: 로그인 → 첨부 → 글 → 공개 페이지·피드 → 정리 → 로그아웃', async () => {
    const json = { ...XRW, Origin: ADMIN_ORIGIN, 'Content-Type': 'application/json' }
    const wrong = await adm('/api/auth/login', { method: 'POST', headers: json, body: JSON.stringify({ password: 'wrong-dummy-value' }) })
    assert.equal(wrong.status, 401)
    const noOrigin = await adm('/api/auth/login', { method: 'POST', headers: { ...XRW, 'Content-Type': 'application/json' }, body: JSON.stringify({ password: SMOKE_ADMIN_PASSWORD }) })
    assert.equal(noOrigin.status, 403)

    const login = await adm('/api/auth/login', { method: 'POST', headers: json, body: JSON.stringify({ password: SMOKE_ADMIN_PASSWORD }) })
    assert.equal(login.status, 204)
    const setCookie = login.headers['set-cookie']?.find(value => value.startsWith('__Host-AdminSession='))
    assert.ok(setCookie, '세션 쿠키')
    for (const attribute of ['secure', 'httponly', 'samesite=strict', 'path=/']) {
      assert.ok(setCookie.toLowerCase().includes(attribute), `쿠키 속성 ${attribute}`)
    }
    assert.ok(!/domain=/i.test(setCookie), '__Host- 쿠키에는 Domain이 없어야 한다')
    const Cookie = setCookie.split(';')[0]
    const authed = { ...XRW, Origin: ADMIN_ORIGIN, Cookie }

    // 클라이언트가 보낸 X-Forwarded-For는 Caddy가 버린다 — 허용 목록 밖 주소를 적어 보내도 판정은 실제 주소(허용)로 난다.
    assert.equal((await adm('/api/posts', { headers: { ...authed, 'X-Forwarded-For': '203.0.113.9' } })).status, 200)

    const tiny = multipart('smoke.png', makePng(2_000, 1))
    const uploaded = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': tiny.contentType }, body: tiny.body })
    assert.ok([200, 201].includes(uploaded.status), `업로드: ${uploaded.status}`)
    const attachment = JSON.parse(uploaded.body.toString('utf8'))

    const slug = `smoke-${Date.now().toString(36)}`
    const created = await adm('/api/posts', {
      method: 'POST', headers: { ...authed, 'Content-Type': 'application/json' },
      body: JSON.stringify({ slug, title: '스모크 <글> & 제목', summary: '배포 스모크', contentMarkdown: `본문 ![그림](${attachment.url})`, tagNames: [], seriesId: null, seriesOrder: null }),
    })
    assert.equal(created.status, 201, created.body.toString('utf8'))
    const post = JSON.parse(created.body.toString('utf8'))

    try {
      const page = await pub(`/posts/${slug}`)
      assert.equal(page.status, 200)
      assert.ok(page.body.toString('utf8').includes(`<img src="${attachment.url}"`), '공개 페이지에 첨부 이미지')
      for (const host of [DOMAIN, ADMIN_DOMAIN]) {
        const image = await send({ host, path: attachment.url })
        assert.equal(image.status, 200, `${host} 첨부`)
        assert.equal(image.headers['content-security-policy'], "default-src 'none'; sandbox", `${host} 첨부 CSP(덮어쓰이면 안 된다)`)
        assert.equal(image.headers['content-type'], 'image/png')
        assert.equal(image.headers['cache-control'], 'public, max-age=31536000, immutable')
      }
      const feed = (await pub('/feed.xml')).body.toString('utf8')
      assert.ok(feed.includes(`${PUBLIC_ORIGIN}/posts/${slug}`), '피드의 절대 URL은 설정된 공개 origin이다')
      assert.ok(!feed.includes('http://api') && !feed.includes(':8080'), '피드에 내부 주소가 새면 안 된다')
    } finally {
      assert.equal((await adm(`/api/posts/${post.id}?version=${post.version}`, { method: 'DELETE', headers: authed })).status, 204)
      assert.equal((await adm(`/api/attachments/${attachment.id}`, { method: 'DELETE', headers: authed })).status, 204)
    }

    // 업로드 크기 경계: Caddy의 상한(11MiB)이 앱의 상한(10MiB)보다 먼저 정상 업로드를 끊지 않는다.
    const nearLimit = multipart('near.png', makePng(10 * MIB - 64 * 1024, 2))
    const accepted = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': nearLimit.contentType }, body: nearLimit.body })
    assert.equal(accepted.status, 201, '10MiB 바로 아래는 통과해야 한다')
    assert.equal((await adm(`/api/attachments/${JSON.parse(accepted.body.toString('utf8')).id}`, { method: 'DELETE', headers: authed })).status, 204)
    const overApp = multipart('over.png', makePng(10 * MIB + 256 * 1024, 3))
    const rejected = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': overApp.contentType }, body: overApp.body })
    assert.equal(rejected.status, 413)
    assert.equal(JSON.parse(rejected.body.toString('utf8')).title, '첨부가 너무 큽니다', '10MiB를 조금 넘으면 앱이 설명과 함께 거부한다')
    const huge = multipart('huge.png', makePng(12 * MIB, 4))
    const cut = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': huge.contentType }, chunks: [huge.body.subarray(0, MIB), huge.body.subarray(MIB)] })
    assert.equal(cut.status, 413, '길이를 알리지 않은(chunked) 12MiB도 5xx 없이 413')

    assert.equal((await adm('/api/auth/logout', { method: 'POST', headers: authed })).status, 204)
    assert.equal((await adm('/api/posts', { headers: authed })).status, 401, '로그아웃 뒤에는 복사해 둔 쿠키가 통하지 않는다')
  })
}

if (ROLE === 'denied') {
  test('관리 사이트: 허용 목록 밖에서는 모든 경로가 본문 없는 404다(X-Forwarded-For를 위조해도)', async () => {
    const forged = { ...XRW, 'X-Forwarded-For': '172.30.0.10', 'X-Real-IP': '172.30.0.10', Forwarded: 'for=172.30.0.10' }
    for (const path of ['/', '/login', '/index.html', '/attachments', '/assets/index.js', '/api/auth/me', '/api/posts', '/attachments/a/b.png', '/health']) {
      assertEmpty404(await adm(path, { headers: forged }), `GET ${path}`)
    }
    const login = await adm('/api/auth/login', { method: 'POST', headers: { ...forged, Origin: ADMIN_ORIGIN, 'Content-Type': 'application/json' }, body: JSON.stringify({ password: SMOKE_ADMIN_PASSWORD }) })
    assertEmpty404(login, 'POST /api/auth/login')
  })

  test('관리 사이트: 공개 도메인의 TLS 이름(SNI)으로 들어와 Host만 관리 호스트로 바꿔도 404다', async () => {
    const response = await send({ host: DOMAIN, servername: DOMAIN, path: '/api/auth/me', headers: { ...XRW, Host: ADMIN_DOMAIN } })
    assert.equal(response.status, 404)
    assert.equal(response.body.length, 0)
  })

  test('공개 사이트는 허용 목록 밖에서도 보인다', async () => {
    assert.equal((await pub('/')).status, 200)
    assert.equal((await pub('/health')).status, 200)
  })

  test('Caddy를 건너뛰고 api에 직접 붙어도 위조한 X-Forwarded-For는 통하지 않는다', async () => {
    // 이 컨테이너는 신뢰 프록시(Caddy의 고정 IP)가 아니므로 앱은 전달 헤더를 버리고 실제 주소(허용 목록 밖)로 판정한다.
    const response = await send({ host: 'api', port: 8080, tls: false, path: '/api/auth/me', headers: { ...XRW, Host: ADMIN_DOMAIN, 'X-Forwarded-For': '172.30.0.10', 'X-Forwarded-Proto': 'https' } })
    assert.equal(response.status, 403)
  })

  test('DB는 edge 네트워크에서 닿지 않는다', async () => {
    const outcome = await new Promise(resolve => {
      const socket = net.connect({ host: 'postgres', port: 5432 })
      socket.setTimeout(3000, () => { socket.destroy(); resolve('timeout') })
      socket.on('connect', () => { socket.destroy(); resolve('connected') })
      socket.on('error', error => resolve(error.code))
    })
    assert.notEqual(outcome, 'connected')
  })
}

// 복원 리허설: seed가 글과 첨부를 남기고, 백업 → 볼륨 삭제 → 복원 뒤에 verify-restore가 같은 내용이 돌아왔는지 본다.
const REHEARSAL_SLUG = 'restore-rehearsal'
const rehearsalPng = () => makePng(200_000, 7)
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex')

if (ROLE === 'seed') {
  test('복원 리허설용 글과 첨부를 남긴다', async () => {
    const base = { ...XRW, Origin: ADMIN_ORIGIN }
    const login = await adm('/api/auth/login', { method: 'POST', headers: { ...base, 'Content-Type': 'application/json' }, body: JSON.stringify({ password: SMOKE_ADMIN_PASSWORD }) })
    assert.equal(login.status, 204)
    const authed = { ...base, Cookie: login.headers['set-cookie'].find(value => value.startsWith('__Host-AdminSession=')).split(';')[0] }
    const form = multipart('rehearsal.png', rehearsalPng())
    const uploaded = await adm('/api/attachments', { method: 'POST', headers: { ...authed, 'Content-Type': form.contentType }, body: form.body })
    assert.ok([200, 201].includes(uploaded.status), `업로드: ${uploaded.status}`)
    const attachment = JSON.parse(uploaded.body.toString('utf8'))
    assert.equal(attachment.sha256, sha256(rehearsalPng()), '메타데이터가 없는 PNG는 바이트 그대로 저장된다(verify-restore가 이 값에 기댄다)')
    const created = await adm('/api/posts', {
      method: 'POST', headers: { ...authed, 'Content-Type': 'application/json' },
      body: JSON.stringify({ slug: REHEARSAL_SLUG, title: '복원 리허설', summary: '백업에서 돌아와야 하는 글', contentMarkdown: `![그림](${attachment.url})`, tagNames: ['복원'], seriesId: null, seriesOrder: null }),
    })
    assert.equal(created.status, 201, created.body.toString('utf8'))
  })
}

if (ROLE === 'verify-restore') {
  test('복원된 스택에 글과 첨부가 바이트 그대로 돌아왔다', async () => {
    const page = await pub(`/posts/${REHEARSAL_SLUG}`)
    assert.equal(page.status, 200)
    const url = /<img src="(\/attachments\/[^"]+)"/.exec(page.body.toString('utf8'))?.[1]
    assert.ok(url, '복원된 글에 첨부 이미지가 있어야 한다')
    const image = await pub(url)
    assert.equal(image.status, 200)
    assert.equal(sha256(image.body), sha256(rehearsalPng()))
    assert.equal((await pub(`/tags/${encodeURIComponent('복원')}`)).status, 200)
  })
}
