import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'
import { takeUnexpectedCalls } from './unexpected'

// vitest의 globals를 켜지 않았으므로 Testing Library의 자동 정리가 등록되지 않는다(실측: 앞 테스트의 DOM이 남아 "multiple elements").
afterEach(() => cleanup())

// stubApi 표에 없는 호출은 request()가 네트워크 오류로 바꿔 삼켜서 그 자체로는 테스트를 실패시키지 않는다 —
// 여기서 테스트마다 직접 실패시켜 표 누락을 조용히 통과시키지 않는다.
afterEach(() => {
  const calls = takeUnexpectedCalls()
  if (calls.length > 0) throw new Error('stubApi 표에 없는 호출: ' + calls.join(', '))
})
