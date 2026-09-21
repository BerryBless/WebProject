import { MutationCache, QueryCache, QueryClient } from '@tanstack/react-query'
import { ApiError } from '../api/errors'
import type { AuthStatus } from '../api/types'

export const ME_KEY = ['auth', 'me'] as const

/**
 * 어느 호출에서든 401(세션 만료·폐기)을 받으면 "로그인 안 됨"으로 기록한다. 화면 전환은 RequireAuth가 이 값을 보고 한다 —
 * 라우터 밖에서 location을 직접 바꾸지 않는다(임시본은 localStorage에 있으므로 로그인 뒤 그대로 복원된다).
 */
export function noteAuthFailure(client: QueryClient, error: unknown): void {
  if (error instanceof ApiError && error.status === 401) client.setQueryData<AuthStatus>(ME_KEY, { authenticated: false })
}

export function createQueryClient(): QueryClient {
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({ onError: error => noteAuthFailure(client, error) }),
    mutationCache: new MutationCache({ onError: error => noteAuthFailure(client, error) }),
    defaultOptions: {
      // 자동 재시도 없음: 429·503에는 Retry-After가 있고, 4xx는 다시 보내도 같다. 다시 시도는 사용자가 버튼으로 한다.
      // 창 포커스 재조회 없음: 편집 중인 글이 포커스만으로 다시 불려 와 입력을 덮어쓰면 안 된다.
      queries: { retry: false, refetchOnWindowFocus: false, staleTime: 0 },
      mutations: { retry: false },
    },
  })
  return client
}
