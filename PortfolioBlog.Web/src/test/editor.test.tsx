import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Attachment, PostDetail } from '../api/types'
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

  it('저장 성공 직후 저장 전 내용을 담은 낡은 임시본이 남지 않는다', async () => {
    // 자동 저장 effect의 의존성에 baseline이 있으면, 저장 성공으로 setBaseline이 새 객체를 만드는 순간 같은
    // 커밋에서 effect가 한 번 더 돌고, 그때 settled는 아직 디바운스 전 값(저장 전 내용)이다 — 그 값이 새
    // baseVersion과 함께 임시본으로 다시 쓰인다(실측).
    stubApi({ ...COMMON, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST }, [`PUT /api/posts/${POST.id}`]: { status: 200, body: { ...POST, contentMarkdown: '# 고친 본문', version: 8 } } })
    renderApp(`/posts/${POST.id}`)
    fireEvent.change(await screen.findByLabelText('본문(마크다운)'), { target: { value: '# 고친 본문' } })
    await userEvent.click(screen.getByRole('button', { name: '저장' })) // 1초 디바운스가 끝나기 전에 저장한다
    await waitFor(() => expect(screen.getByRole('button', { name: '저장' })).toBeDisabled())
    expect(loadDraft(POST.id)).toBeNull()
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

  it('여러 이미지를 올릴 때 파일별 오류를 모아 보여주고, 서버 오류가 나면 남은 파일은 올리지 않는다', async () => {
    const calls = stubApi({ ...COMMON, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST }, 'POST /api/attachments': { status: 500 } })
    renderApp(`/posts/${POST.id}`)
    // input의 accept 속성 때문에 userEvent.upload는 형식이 다른 파일(예: text/plain)을 아예 골라 주지 않는다
    // (실제 파일 선택 대화상자와 같은 동작) — 그래서 편의 검사 실패는 "형식은 맞지만 크기가 0인 파일"로 재현한다.
    const bad = new File([], 'empty.png', { type: 'image/png' })
    const good1 = new File([new Uint8Array([1, 2, 3])], 'a.png', { type: 'image/png' })
    const good2 = new File([new Uint8Array([1, 2, 3])], 'b.png', { type: 'image/png' }) // 앞의 서버 오류 때문에 시도조차 되지 않아야 한다
    await userEvent.upload(await screen.findByLabelText('이미지 올리기'), [bad, good1, good2])
    await screen.findByText(/빈 파일은 올릴 수 없습니다/)
    expect(screen.getByText(/나머지 1개는 올리지 않았습니다/)).toBeInTheDocument()
    expect(calls.filter(c => c.method === 'POST' && c.url === '/api/attachments')).toHaveLength(1) // good2는 시도되지 않음
  })

  it('업로드가 도는 중에 또 업로드를 걸면 받지 않고 그 사실을 알린다', async () => {
    let resolveFirst!: (reply: { status: number; body: Attachment }) => void
    const firstPromise = new Promise<{ status: number; body: Attachment }>(resolve => { resolveFirst = resolve })
    const attachment: Attachment = { id: 'a1', url: '/attachments/a.png', fileName: 'a.png', contentType: 'image/png', sizeBytes: 3, sha256: 'x', createdAt: '2026-09-01T00:00:00Z' }
    const calls = stubApi({ ...COMMON, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST }, 'POST /api/attachments': () => firstPromise })
    renderApp(`/posts/${POST.id}`)
    const input = await screen.findByLabelText('이미지 올리기')
    const file1 = new File([new Uint8Array([1, 2, 3])], 'a.png', { type: 'image/png' })
    const file2 = new File([new Uint8Array([1, 2, 3])], 'b.png', { type: 'image/png' })
    await userEvent.upload(input, file1) // 아직 응답 전(firstPromise가 안 풀림)
    await userEvent.upload(input, file2) // 그 사이 또 업로드를 건다 — 파일 선택이 겹치는 상황(붙여넣기·드롭과의 경합)을 흉내낸다
    expect(await screen.findByText(/이미 업로드가 진행 중입니다/)).toBeInTheDocument()
    resolveFirst({ status: 201, body: attachment })
    await waitFor(() => expect(calls.filter(c => c.method === 'POST' && c.url === '/api/attachments')).toHaveLength(1)) // 두 번째 호출은 나가지 않았다
  })

  it('저장 요청이 돌아오기 전에 친 글자는 남는다(수정 화면)', async () => {
    let resolvePut!: (reply: { status: number; body: PostDetail }) => void
    const putPromise = new Promise<{ status: number; body: PostDetail }>(resolve => { resolvePut = resolve })
    const calls = stubApi({
      ...COMMON, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST },
      [`PUT /api/posts/${POST.id}`]: () => putPromise,
    })
    renderApp(`/posts/${POST.id}`)
    await userEvent.clear(await screen.findByLabelText(/^제목/))
    await userEvent.type(screen.getByLabelText(/^제목/), '바뀐 제목')
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    // 요청이 도는 동안(아직 응답 전) 다른 필드를 더 친다 — 응답이 이 입력을 덮으면 안 된다.
    await userEvent.clear(screen.getByLabelText(/^요약/))
    await userEvent.type(screen.getByLabelText(/^요약/), '요약수정중')
    resolvePut({ status: 200, body: { ...POST, title: '바뀐 제목', version: 8 } })
    await waitFor(() => expect(screen.getByRole('button', { name: '저장' })).not.toBeDisabled()) // 응답 뒤에도 dirty(더 바뀐 게 있음)
    expect(screen.getByLabelText(/^요약/)).toHaveValue('요약수정중') // 사라지지 않았다
    // 임시본도 화면과 같은 내용을 담아야 한다 — 자동 저장 틱을 기다리지 않고 onSuccess가 지금 내용으로 바로 쓴다.
    expect(loadDraft(POST.id)).toMatchObject({ summary: '요약수정중', baseVersion: 8 })
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    await waitFor(() => expect(calls.filter(c => c.method === 'PUT')).toHaveLength(2))
    expect(calls.filter(c => c.method === 'PUT')[1].body).toMatchObject({ version: 8, summary: '요약수정중' }) // 다음 저장은 최신 version을 그대로 싣는다
  })

  it('새 글: 생성 요청이 도는 동안 친 내용은 이동 뒤 임시본 복원으로 남는다', async () => {
    let resolvePost!: (reply: { status: number; body: PostDetail }) => void
    const postPromise = new Promise<{ status: number; body: PostDetail }>(resolve => { resolvePost = resolve })
    stubApi({ ...COMMON, 'POST /api/posts': () => postPromise, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST } })
    renderApp('/posts/new')
    await userEvent.type(await screen.findByLabelText(/^제목/), '새 글 제목')
    await userEvent.type(screen.getByLabelText(/^slug/), 'new-post')
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    await userEvent.type(screen.getByLabelText(/^요약/), '중간에 더 씀') // 요청이 도는 동안 더 친다
    resolvePost({ status: 201, body: { ...POST, slug: 'new-post', title: '새 글 제목' } })
    await screen.findByText(/저장된 임시본이 있습니다/) // 이동한 편집 화면이 복원을 제안한다
    expect(loadDraft(POST.id)?.summary).toBe('중간에 더 씀')
  })

  it('새 글: 생성 요청이 도는 동안 친 내용을 임시본으로 남기지 못하면 이동하지 않고 저장을 막는다', async () => {
    // 여기서 실패하면 이동 직전이라 setDraftFailed(true)를 화면에 보일 틈이 없다(새 Editor가 곧바로 마운트되며
    // 그 상태를 버린다) — 그래서 이 경우만 이동하지 않는다. 서버에는 이미 글이 생겼으므로 "저장"을 막아
    // 다시 눌러도 중복 글이 생기지 않게 한다.
    const setItemSpy = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('quota', 'QuotaExceededError') })
    let resolvePost!: (reply: { status: number; body: PostDetail }) => void
    const postPromise = new Promise<{ status: number; body: PostDetail }>(resolve => { resolvePost = resolve })
    stubApi({ ...COMMON, 'POST /api/posts': () => postPromise, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST } })
    const { router } = renderApp('/posts/new')
    await userEvent.type(await screen.findByLabelText(/^제목/), '새 글 제목')
    await userEvent.type(screen.getByLabelText(/^slug/), 'new-post')
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    await userEvent.type(screen.getByLabelText(/^요약/), '중간에 더 씀') // 요청이 도는 동안 더 친다
    resolvePost({ status: 201, body: { ...POST, slug: 'new-post', title: '새 글 제목' } })
    await screen.findByText(/임시 저장하지 못했습니다/)
    expect(router.state.location.pathname).toBe('/posts/new') // 이동하지 않았다
    expect(screen.getByRole('button', { name: '저장' })).toBeDisabled() // 다시 눌러 중복 글을 만들 수 없다
    setItemSpy.mockRestore()
  })

  it('새 글 생성 성공 뒤에도 저장 전 내용을 담은 낡은 임시본이 남지 않는다', async () => {
    // 자동 저장 effect는 baseline을 의존성으로 보지 않는다(O1) — 저장 성공으로 baseline이 바뀌어도 이 effect가
    // 다시 돌아 저장 전 settled 값을 새 baseVersion과 함께 되쓰지 않는다. 타이핑 직후(1초 디바운스가 끝나기
    // 전) 곧바로 저장해 그 경로를 확인한다.
    stubApi({ ...COMMON, 'POST /api/posts': { status: 201, body: { ...POST, slug: 'new-post' } }, [`GET /api/posts/${POST.id}`]: { status: 200, body: POST } })
    renderApp('/posts/new')
    await userEvent.type(await screen.findByLabelText(/^제목/), '새 글 제목')
    await userEvent.type(screen.getByLabelText(/^slug/), 'new-post')
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    await waitFor(() => expect(loadDraft('new')).toBeNull())
  })

  it('409 뒤 최신본 재조회가 404면(그사이 삭제됨) 사실을 알리고, 충돌 화면은 뜨지 않는다', async () => {
    let gets = 0
    stubApi({
      ...COMMON,
      [`GET /api/posts/${POST.id}`]: () => (gets++ === 0 ? { status: 200, body: POST } : { status: 404 }),
      [`PUT /api/posts/${POST.id}`]: { status: 409, body: { title: '충돌', detail: '다른 곳에서 이 글이 먼저 수정되었습니다.' } },
    })
    renderApp(`/posts/${POST.id}`)
    fireEvent.change(await screen.findByLabelText('본문(마크다운)'), { target: { value: '# 내 본문' } })
    await userEvent.click(screen.getByRole('button', { name: '저장' }))
    await screen.findByText('이 글은 다른 곳에서 삭제되었습니다. 이 화면의 내용은 그대로 있으니 필요하면 복사해 새 글로 저장하세요.')
    expect(screen.queryByRole('alertdialog', { name: '저장 충돌' })).not.toBeInTheDocument()
    expect(screen.getAllByRole('alert')).toHaveLength(1) // 같은 실패에 대한 일반 안내(ErrorNotice)가 겹쳐 뜨지 않는다
  })
})
