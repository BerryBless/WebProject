import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach } from 'vitest'

// vitest의 globals를 켜지 않았으므로 Testing Library의 자동 정리가 등록되지 않는다(실측: 앞 테스트의 DOM이 남아 "multiple elements").
afterEach(() => cleanup())
