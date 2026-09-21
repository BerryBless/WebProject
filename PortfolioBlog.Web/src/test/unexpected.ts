// stubApi가 표에 없는 호출을 만나면 Error를 던지지만, 그 예외는 api/client.ts의 request()가 잡아
// ApiError(0, '네트워크 오류')로 바꾼다(취소가 아닌 모든 fetch 실패를 그렇게 다룬다) — 그래서 예외를 던지는 것만으로는
// 결과를 단언하지 않는 부수 호출이 표에서 빠져도 테스트가 조용히 통과한다. 여기 쌓아 두고 setup.ts의 afterEach가
// 테스트 하나가 끝날 때마다 비어 있는지 확인해 실패시킨다.
const unexpectedCalls: string[] = []

/** 표에 없는 호출 1건을 기록한다. */
export function noteUnexpectedCall(description: string): void {
  unexpectedCalls.push(description)
}

/** 쌓인 호출 목록을 꺼내면서 비운다(다음 테스트로 새지 않게). */
export function takeUnexpectedCalls(): string[] {
  return unexpectedCalls.splice(0, unexpectedCalls.length)
}
