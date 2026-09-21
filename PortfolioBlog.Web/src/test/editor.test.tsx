import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { PostDetail } from '../api/types'
import { loadDraft, saveDraft } from '../lib/drafts'
import { LOGGED_IN, renderApp, stubApi } from './harness'

// CodeMirror는 jsdom에 없는 레이아웃 API에 기대므로 단위 테스트에서는 textarea로 바꾼다. 진짜 편집기는 Playwright E2E가 본다.
vi.mock('../components/MarkdownEditor', () => ({
  MarkdownEditor: ({ initialValue, onChange }: { initialValue: string; onChange: (v: string) => void }) =>
    <textarea aria-label="본문(마크다운)" defaultValue={initialValue} onChange={e => onChange(e.target.value)} />,
}))

const POST: PostDetail = {
  id: '0199aaaa-0000-7000-8000-000000000001', slug: 'hello', title: '안녕', summary: '요약', contentMarkdown: '# 서버 본문',
  tags: ['C#'], seriesId: null, seriesOrder: null, createdAt: '2026-09-01T00:00:00Z', updatedAt: '2026-09-02T00:00:00Z', version: 7,
}
const COMMON = { ...LOGGED_IN, 'GET /api/series': { status: 200, body: [] }, 'GET /api/tags': { status: 200, body: [] }, 'POST /api/preview': { status: 200, body: { html: '<p>ok</p>' } } }

beforeEach(() => window.localStorage.clear())
afterEach(() => vi.unstubAllGlobals())

describe('글 편집', () => {
  it('새 글: "저장하면 즉시 공개됩니다"를 보여 주고, 검증을 통과한 내용만 보낸다', async () => {
    const calls = stubApi({ ...COMMON, 'POST /api/posts': { status: 201, body: { ...POST, slug: 'new-post' } }, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST } })
    const { router } = renderApp('/posts/new')
    expect(await screen.findByText('저장하면 즉시 공개됩니다')).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    expect(await screen.findByText('slug는 필수입니다.')).toBeInTheDocument()
    expect(calls.some(c => c.method === 'POST' && c.url === '/api/posts')).toBe(false) // 클라이언트 검증 실패면 보내지 않는다

    await userEvent.type(screen.getByLabelText(/^제목/), '새 글 제목')
    await userEvent.type(screen.getByLabelText(/^slug/), 'new-post')
    await userEvent.type(screen.getByLabelText('태그 추가'), 'C#{Enter}')
    fireEvent.change(screen.getByLabelText('본문(마크다운)'), { target: { value: '본문 **굵게**' } })
    // 임시본 자동 저장(1초 디바운스)이 실제로 끝난 뒤에 저장해야 아래 loadDraft('new') 단언이 의미가 있다 —
    // 저장이 임시본보다 먼저 끝나면 애초에 임시본이 없어 clearDraft를 지워도 이 단언은 항상 통과한다(거짓 보장).
    await waitFor(() => expect(loadDraft('new')).not.toBeNull(), { timeout: 2000 })
    await userEvent.click(screen.getByRole('button', { name: '저장' }))

    await waitFor(() => expect(router.state.location.pathname).toBe(`/posts/${POST.id}`))
    expect(calls.find(c => c.method === 'POST' && c.url === '/api/posts')?.body).toEqual({
      slug: 'new-post', title: '새 글 제목', summary: '', contentMarkdown: '본문 **굵게**', tagNames: ['C#'], seriesId: null, seriesOrder: null,
    })
    expect(loadDraft('new')).toBeNull() // 저장에 성공하면 임시본을 지운다
  })

  it('수정: slug는 읽기 전용이고, 받은 version을 그대로 돌려보낸다', async () => {
    const calls = stubApi({ ...COMMON, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST }, [`PUT /api/posts/${POST.id}`]: { status: 200, body: { ...POST, title: '바뀐 제목', version: 8 } } })
    renderApp(`/posts/${POST.id}`)
    expect(await screen.findByLabelText(/^slug/)).toHaveAttribute('readonly')
    expect(screen.getByRole('button', { name: '저장' })).toBeDisabled() // 바뀐 것이 없으면 저장하지 않는다
    await userEvent.clear(screen.getByLabelText(/^제목/))
    await userEvent.type(screen.getByLabelText(/^제목/), '바뀐 제목')
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    await waitFor(() => expect(calls.find(c => c.method === 'PUT')).toBeDefined())
    expect(calls.find(c => c.method === 'PUT')?.body).toMatchObject({ title: '바뀐 제목', version: 7, tagNames: ['C#'] })
    await waitFor(() => expect(screen.getByRole('button', { name: '저장' })).toBeDisabled()) // 저장 뒤 기준선이 갱신된다
  })

  it('서버의 400은 필드 옆에 표시한다', async () => {
    stubApi({ ...COMMON, 'POST /api/posts': { status: 400, body: { title: 'validation', errors: { contentMarkdown: ['중첩이 너무 깊습니다.'] } } } })
    renderApp('/posts/new')
    await userEvent.type(await screen.findByLabelText(/^제목/), 't')
    await userEvent.type(screen.getByLabelText(/^slug/), 's')
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    expect(await screen.findByText('중첩이 너무 깊습니다.')).toBeInTheDocument()
  })

  it('409: 최신 서버본을 받아 내 본문과 나란히 보여 주고, 내 변경은 임시본에 남는다', async () => {
    const latest = { ...POST, contentMarkdown: '# 다른 탭에서 고친 본문', version: 9 }
    let gets = 0
    const calls = stubApi({
      ...COMMON,
      [`GET /api/posts/${POST.id}`]: () => ({ status: 200, body: gets++ === 0 ? POST : latest }),
      [`PUT /api/posts/${POST.id}`]: call => (call.body as { version: number }).version === 9
        ? { status: 200, body: { ...latest, contentMarkdown: '# 내 본문', version: 10 } }
        : { status: 409, body: { title: '충돌', detail: '다른 곳에서 이 글이 먼저 수정되었습니다.' } },
    })
    renderApp(`/posts/${POST.id}`)
    fireEvent.change(await screen.findByLabelText('본문(마크다운)'), { target: { value: '# 내 본문' } })
    await userEvent.click(screen.getByRole('button', { name: '저장' }))

    const panel = await screen.findByRole('alertdialog', { name: '저장 충돌' })
    expect(within(panel).getByLabelText('서버본')).toHaveValue('# 다른 탭에서 고친 본문')
    expect(within(panel).getByLabelText('내 본문')).toHaveValue('# 내 본문')
    expect(screen.getByRole('button', { name: '저장' })).toBeDisabled() // 고르기 전에는 저장할 수 없다

    await userEvent.click(within(panel).getByRole('button', { name: /내 내용 유지/ }))
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    await waitFor(() => expect(calls.filter(c => c.method === 'PUT')).toHaveLength(2))
    expect(calls.filter(c => c.method === 'PUT')[1].body).toMatchObject({ contentMarkdown: '# 내 본문', version: 9 })
  })

  it('임시본: 자동 저장되고, 다시 열면 복원을 묻는다(자동으로 덮어쓰지 않는다)', async () => {
    stubApi({ ...COMMON, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST } })
    saveDraft(POST.id, { slug: 'hello', title: '안녕', summary: '요약', contentMarkdown: '# 임시본 본문', tagNames: ['C#'], seriesId: null, seriesOrder: null, baseVersion: 6, savedAt: '2026-09-03T00:00:00.000Z' })
    renderApp(`/posts/${POST.id}`)
    const body = await screen.findByLabelText('본문(마크다운)')
    expect(body).toHaveValue('# 서버 본문')
    expect(screen.getByText(/그 뒤에 서버본이 바뀌었습니다/)).toBeInTheDocument() // baseVersion 6 ≠ 서버 7
    expect(loadDraft(POST.id)?.contentMarkdown).toBe('# 임시본 본문')            // 고르기 전에는 임시본을 건드리지 않는다
    await userEvent.click(screen.getByRole('button', { name: '임시본 복원' }))
    expect(await screen.findByLabelText('본문(마크다운)')).toHaveValue('# 임시본 본문')
  })

  it('이미지 업로드 중 401이 오면 로그인 화면으로 간다', async () => {
    // uploadImages는 useMutation을 거치지 않고 attachments.upload를 직접 await한다 — MutationCache.onError를
    // 타지 않으므로 401을 스스로 noteAuthFailure로 넘겨야 한다(넘기지 않으면 세션이 만료돼도 이 화면에 그대로 남는다).
    stubApi({ ...COMMON, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST }, 'POST /api/attachments': { status: 401 } })
    renderApp(`/posts/${POST.id}`)
    const file = new File([new Uint8Array([1, 2, 3])], 'photo.png', { type: 'image/png' })
    await userEvent.upload(await screen.findByLabelText('이미지 올리기'), file)
    await screen.findByRole('heading', { name: '관리자 로그인' })
  })
})
