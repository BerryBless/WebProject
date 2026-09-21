import { useQuery } from '@tanstack/react-query'
import { Navigate, Outlet, useLocation } from 'react-router'
import { auth } from '../api/endpoints'
import { ME_KEY } from '../app/queryClient'
import { ErrorNotice, Loading } from '../components/notices'

/**
 * 로그인한 세션에만 하위 라우트를 보여 준다. 이것은 **화면 편의**다 — 권한의 실제 판정은 서버가 요청마다 한다
 * (호스트·IP·CSRF 헤더·Origin·세션). 이 컴포넌트를 우회해도 API는 401을 돌려준다.
 */
export function RequireAuth() {
  const location = useLocation()
  const me = useQuery({ queryKey: ME_KEY, queryFn: ({ signal }) => auth.me(signal) })
  if (me.isPending) return <Loading label="세션 확인 중…" />
  if (me.isError) return <div className="p-4"><ErrorNotice error={me.error} onRetry={() => void me.refetch()} /></div>
  if (!me.data.authenticated) {
    const next = location.pathname + location.search
    return <Navigate to={`/login?next=${encodeURIComponent(next)}`} replace />
  }
  return <Outlet />
}
