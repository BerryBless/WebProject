import { screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { LOGGED_IN, renderApp, stubApi } from './harness'

// 편집 화면 모듈의 평가 자체가 던지게 한다 — 배포로 청크 해시가 바뀐 뒤 열려 있던 탭이 그 화면을 처음 열 때와 같은
// 모양이다(지연 import가 실패한다). 이 모킹은 파일 전체에 걸리므로 다른 편집기 테스트와 파일을 나눈다.
vi.mock('../pages/PostEditorPage', () => { throw new Error('boom-secret') })

afterEach(() => vi.unstubAllGlobals())

describe('라우트 오류 경계', () => {
  it('화면을 불러오지 못하면 안내와 돌아갈 길을 주고, 오류 내용은 그리지 않는다', async () => {
    stubApi({ ...LOGGED_IN })
    renderApp('/posts/new')
    expect(await screen.findByText(/화면을 불러오지 못했습니다/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: '새로고침' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '글 목록으로' })).toBeInTheDocument()
    expect(document.body.textContent).not.toContain('boom-secret')            // 오류 메시지를 화면에 쓰지 않는다
    expect(document.body.textContent).not.toContain('Unexpected Application') // react-router 기본 화면이 아니다
  })
})
