/** C0 제어 문자(0x00~0x1F)나 DEL(0x7F)이 있는가. 정규식의 유니코드 이스케이프 대신 코드 값으로 비교한다(저장소 규칙: NUL 표기 금지). */
export function hasControlChar(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const code = value.charCodeAt(i)
    if (code < 0x20 || code === 0x7f) return true
  }
  return false
}

/**
 * 로그인 뒤 돌아갈 경로(?next=)를 검증한다. 같은 출처의 절대 경로만 통과시키고 나머지는 '/'로 바꾼다(오픈 리다이렉트 방지).
 * 거부: 스킴·호스트가 있는 값, '//'·'/\' 시작(브라우저가 호스트로 해석), 제어 문자, 로그인 화면 자신(루프).
 */
export function safeNext(raw: string | null | undefined, origin: string = window.location.origin): string {
  if (!raw || raw.length > 2048) return '/'
  if (!raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) return '/'
  if (hasControlChar(raw)) return '/'
  let url: URL
  try { url = new URL(raw, origin) } catch { return '/' }
  if (url.origin !== origin) return '/'
  if (url.pathname === '/login') return '/'
  return url.pathname + url.search + url.hash
}
