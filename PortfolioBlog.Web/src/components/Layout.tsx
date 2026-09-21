import { useMutation, useQueryClient } from '@tanstack/react-query'
import { NavLink, Outlet } from 'react-router'
import { auth } from '../api/endpoints'
import type { AuthStatus } from '../api/types'
import { ME_KEY } from '../app/queryClient'
import { ErrorNotice } from './notices'

const LINKS = [['/', '글'], ['/series', '시리즈'], ['/tags', '태그'], ['/attachments', '첨부']] as const

export function Layout() {
  const client = useQueryClient()
  const logout = useMutation({
    mutationFn: () => auth.logout(),
    onSuccess: () => {
      // "로그인 안 됨"을 기록하면 RequireAuth가 로그인 화면으로 보낸다. client.clear()는 쓰지 않는다(실측): 쿼리 객체를 통째로
      // 없애면 RequireAuth의 관찰자가 옛 객체에 매달린 채 남아 새 값을 보지 못하고 화면이 그대로 있다.
      client.setQueryData<AuthStatus>(ME_KEY, { authenticated: false })
      // 다른 사람이 같은 브라우저로 로그인했을 때 앞사람의 목록이 캐시에서 보이지 않게 나머지는 지운다. 임시본은 남긴다.
      client.removeQueries({ predicate: query => query.queryKey[0] !== ME_KEY[0] })
    },
  })
  return (
    <div className="min-h-screen">
      <header className="flex items-center gap-4 border-b px-4 py-2">
        <strong>블로그 관리</strong>
        <nav className="flex gap-3 text-sm">
          {LINKS.map(([to, label]) => (
            <NavLink key={to} to={to} end={to === '/'} className={({ isActive }) => isActive ? 'font-bold underline' : ''}>{label}</NavLink>
          ))}
        </nav>
        <span className="flex-1" />
        {/* 로그아웃은 서버의 SessionEpoch를 올린다 — 다른 기기·탭의 세션도 전부 끝난다. */}
        <button type="button" className="text-sm underline" disabled={logout.isPending} onClick={() => logout.mutate()} title="모든 기기의 세션이 함께 끝납니다">
          로그아웃
        </button>
      </header>
      {logout.isError && <div className="p-4"><ErrorNotice error={logout.error} /></div>}
      <Outlet />
    </div>
  )
}
