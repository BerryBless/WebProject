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
  "img-src 'self' blob:",
  "connect-src 'self'",
  "font-src 'self'",
  "frame-src 'self'",
  "base-uri 'none'",
  "form-action 'none'",
  "frame-ancestors 'none'",
].join('; ')

export const ADMIN_SECURITY_HEADERS: Record<string, string> = {
  'Content-Security-Policy': ADMIN_CSP,
  'X-Content-Type-Options': 'nosniff',
  'X-Frame-Options': 'DENY',
  'Referrer-Policy': 'strict-origin-when-cross-origin',
  // PortfolioBlog.Api의 SecurityHeadersMiddleware.PermissionsPolicy와 같은 값.
  'Permissions-Policy': 'accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), usb=(), xr-spatial-tracking=()',
}
