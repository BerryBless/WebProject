import { existsSync, readFileSync } from 'node:fs'
import { defineConfig, type ProxyOptions } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { ADMIN_SECURITY_HEADERS } from './admin-headers.ts'

// 백엔드(PortfolioBlog.Api)의 주소. 세션 쿠키가 Secure + __Host- 라서 SPA도 HTTPS로 띄운다(개발·미리보기 공통).
const API_ORIGIN = process.env.BLOG_API_ORIGIN ?? 'https://localhost:7198'
const CERT = '.certs/dev.pem'
const KEY = '.certs/dev.key'

// changeOrigin: false — Host 헤더를 SPA 출처 그대로 넘긴다. 백엔드의 호스트 필터는 포트를 뺀 호스트(localhost)만 보고,
// Origin 검사는 Site:AdminOrigin과 비교한다. 그래서 백엔드는 Site__AdminOrigin=<이 SPA의 출처>로 띄워야 한다(README).
// secure: false — 개발 인증서는 OS 저장소에서만 신뢰된다(Node는 OS 저장소를 보지 않는다). 루프백 전용 설정이다.
// 키를 정규식으로 쓴다(^로 시작하면 Vite가 정규식으로 읽는다). 문자열 키는 접두사 매칭이라 SPA 라우트
// `/attachments`(첨부 화면)의 전체 로드·새로고침이 백엔드로 가 404 JSON이 되고, `/apix` 같은 경로도 함께 끌려간다.
// 운영(Caddy)은 `path /api/* /attachments/*`로 가르므로 개발·미리보기도 같은 경계를 쓴다.
const proxy: Record<string, ProxyOptions> = {
  '^/api/': { target: API_ORIGIN, changeOrigin: false, secure: false },
  '^/attachments/': { target: API_ORIGIN, changeOrigin: false, secure: false },
}

function https() {
  if (!existsSync(CERT) || !existsSync(KEY)) throw new Error('개발 인증서가 없습니다. 먼저 `npm run certs`를 실행하세요(.NET 개발 인증서를 .certs/로 내보냅니다).')
  return { cert: readFileSync(CERT), key: readFileSync(KEY) }
}

export default defineConfig(({ command }) => ({
  base: '/',
  plugins: [react(), tailwindcss()],
  build: { sourcemap: false, chunkSizeWarningLimit: 700 },
  // 인증서는 서버를 띄울 때만 읽는다 — `vite build`(CI의 web 잡)는 인증서 없이 돌아야 한다.
  server: command === 'serve' ? { host: 'localhost', port: 5173, strictPort: true, https: https(), proxy } : undefined,
  // preview는 production 빌드를 실제 보안 헤더와 함께 내보낸다: E2E가 "배포될 CSP 아래에서 위반 0건"을 검사하는 자리다.
  preview: command === 'serve' ? { host: 'localhost', port: 4173, strictPort: true, https: https(), proxy, headers: ADMIN_SECURITY_HEADERS } : undefined,
}))
