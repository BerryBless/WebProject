// 미리보기 iframe(srcdoc)에 넣을 문서를 만든다. 서버가 정제한 HTML은 **여기에만** 들어간다 — React DOM에는 절대 넣지 않는다.
//
// 방어는 세 겹이다: (1) 서버의 마크다운 파이프라인이 이미 정제했다, (2) iframe sandbox=""(토큰 없음)라 스크립트·폼·팝업·
// 같은 출처 접근이 전부 꺼진다, (3) 문서 안 CSP가 default-src 'none'이라 이 출처의 이미지·스타일시트 말고는 아무것도 못 읽는다.
//
// sandbox를 빼면 (2)가 사라지지만 나머지가 각각 다른 것을 막는다(실측): 스크립트와 리소스는 이 문서 안 CSP가 막고,
// 프레임 자신의 이동(meta refresh·링크)은 부모 문서의 CSP frame-src 'self'가 막는다. 부모 CSP까지 없으면
// Chromium에서 meta refresh가 이 iframe을 외부 주소로 이동시켰다.
//
// CSP에 'self'를 쓰지 않는 이유: Firefox는 about:srcdoc 문서의 'self'를 부모 출처로 보지 않아 스타일시트와 이미지를
// 모두 차단한다(저장소 밖 Playwright 측정). Chromium은 허용한다. 출처를 명시하면 Chromium·Firefox 둘 다 스타일시트·
// 이미지를 로드한다 — 이 긍정 명제는 이 저장소의 E2E(admin.spec.ts)가 매번 확인한다. 위 CSP를 'self'로 되돌리면
// Firefox에서 그 확인이 깨진다는 것은 한 번 관측한 사실이며, 상시 확인 대상은 아니다.

// IPv6 리터럴 호스트([::1] 등)를 대괄호째 허용한다 — window.location.origin이 실제로 이 형태일 수 있다(예: https://[::1]:5173).
const ORIGIN_PATTERN = /^https?:\/\/(\[[0-9a-f:]+\]|[a-z0-9.-]+)(:\d{1,5})?$/i

/**
 * 공개 사이트와 같은 모양으로 보이게 하는 스타일시트(공개 사이트 CSS의 스냅숏 — 원본: PortfolioBlog.Api/wwwroot/css/site.css와 서버가 생성하는 강조 CSS).
 * 사본이 원본과 같은지는 PortfolioBlog.Api.Tests의 PreviewCssSnapshotTests가 검사한다.
 */
export const PREVIEW_STYLESHEETS = ['/preview/site.css', '/preview/highlight.css'] as const

export function previewCsp(origin: string): string {
  // origin은 window.location.origin에서만 온다. 그래도 CSP 문자열에 끼워 넣기 전에 모양을 검사한다(따옴표·세미콜론·공백 주입 차단).
  if (!ORIGIN_PATTERN.test(origin)) throw new Error('미리보기 CSP에 쓸 수 없는 출처입니다.')
  return `default-src 'none'; img-src ${origin}; style-src ${origin}; base-uri 'none'; form-action 'none'`
}

export function buildPreviewDocument(html: string, origin: string = window.location.origin): string {
  const links = PREVIEW_STYLESHEETS.map(href => `<link rel="stylesheet" href="${href}">`).join('')
  // CSP meta는 <head>의 첫 요소여야 뒤따르는 <link>에도 적용된다.
  return '<!doctype html><html lang="ko"><head>' +
    `<meta http-equiv="Content-Security-Policy" content="${previewCsp(origin)}">` +
    `<meta charset="utf-8">${links}</head>` +
    `<body><main><article><div class="article-body">${html}</div></article></main></body></html>`
}
