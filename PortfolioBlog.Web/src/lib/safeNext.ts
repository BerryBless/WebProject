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
 * `new URL`이 입력을 정규화(`.`·`..`·백슬래시 처리)하면서 선행 '//'를 새로 만들 수 있다(예: '/.//evil.test',
 * '/x/..//evil.test', '/%2e%2e//evil.test' 는 앞부분만 보면 '/'로 시작하지만 pathname이 '//evil.test'가 되어
 * 프로토콜 상대(=교차 출처) URL이 된다). 그래서 조립한 반환값도 돌려주기 직전에 같은 모양으로 다시 검사한다 —
 * 이 함수의 반환값은 라우터·링크 어디에 들어가도 그 자체로 같은 출처 경로여야 하고, 호출부(라우터)의 내부 검사에 기대지 않는다.
 */
export function safeNext(raw: string | null | undefined, origin: string = window.location.origin): string {
  if (!raw || raw.length > 2048) return '/'
  if (!raw.startsWith('/') || raw.startsWith('//') || raw.startsWith('/\\')) return '/'
  if (hasControlChar(raw)) return '/'
  let url: URL
  try { url = new URL(raw, origin) } catch { return '/' }
  if (url.origin !== origin) return '/'
  // 라우터 매칭은 대소문자·끝 슬래시를 무시하므로(react-router 기본 동작) '/LOGIN'·'/login/'도 같은 화면으로 본다.
  if (url.pathname.replace(/\/+$/, '').toLowerCase() === '/login') return '/'
  const backslash = String.fromCharCode(92)
  const path = url.pathname + url.search + url.hash
  // 반환 직전 재검사: 위의 origin 비교를 통과했더라도 new URL의 정규화(.·..·백슬래시 처리)가 선행 '//'를
  // 새로 만들 수 있다('/.//evil.test', '/x/..//evil.test', '/%2e%2e//evil.test' 전부 pathname이 '//evil.test'가
  // 된다). 이 함수의 반환값은 어디에 꽂혀도 그 자체로 같은 출처 경로여야 한다(호출부의 검사에 기대지 않는다).
  if (path.startsWith('//') || path.startsWith('/' + backslash)) return '/'
  return path
}
