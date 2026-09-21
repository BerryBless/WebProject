// 관리 SPA 문서에 붙는 보안 헤더의 정본. 지금은 `vite preview`(E2E)가 쓰고, Plan 4의 Caddyfile이 같은 값을 관리 사이트 블록에 옮긴다.
// 값을 바꾸면 E2E의 "CSP 위반 0건" 검사를 Chromium·Firefox 양쪽에서 다시 통과시켜야 한다.
//
// 스펙 3.6보다 좁다(실측으로 좁힌 것):
// - default-src 'none' + 필요한 것만 나열(connect-src·font-src를 'self'로 명시).
// - style-src를 요소와 속성으로 나눴다. CodeMirror는 <style> 요소 하나를 주입하므로 style-src-elem에만 'unsafe-inline'을 둔다.
//   style 속성(style-src-attr)은 막는다 — React의 style prop과 CodeMirror의 치수 지정은 CSSOM으로 들어가 CSP 대상이 아니다.
// - form-action 'none': 이 SPA는 폼을 서버로 제출하지 않는다(전부 fetch).
export const ADMIN_CSP = [
  "default-src 'none'",
  "script-src 'self'",
  "style-src-elem 'self' 'unsafe-inline'",
  "style-src-attr 'none'",
  "img-src 'self'", // blob:은 쓰는 곳이 없다(URL.createObjectURL 호출 0건) — 쓰지 않는 출처는 열어 두지 않는다
  "connect-src 'self'",
  "font-src 'self'",
  "frame-src 'self'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join('; ')

// Strict-Transport-Security는 의도적으로 없다: 이 값은 루프백(`vite preview`, E2E)에서도 그대로 나가는데,
// localhost에 HSTS를 걸면 그 헤더를 받은 개발자 브라우저 프로필의 루프백 전체가 이후 HTTPS로 고정된다.
// 운영에서는 Plan 4의 Caddyfile이 관리 사이트 블록에 HSTS를 별도로 더한다(이 파일에는 없다).
export const ADMIN_SECURITY_HEADERS: Record<string, string> = {
  'Content-Security-Policy': ADMIN_CSP,
  'X-Content-Type-Options': 'nosniff',
  'X-Frame-Options': 'DENY',
  'Referrer-Policy': 'strict-origin-when-cross-origin',
  // PortfolioBlog.Api의 SecurityHeadersMiddleware.PermissionsPolicy와 같은 값.
  'Permissions-Policy': 'accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), usb=(), xr-spatial-tracking=()',
}
