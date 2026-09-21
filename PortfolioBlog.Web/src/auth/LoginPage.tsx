import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useNavigate, useSearchParams } from 'react-router'
import { auth } from '../api/endpoints'
import { ApiError, describeError } from '../api/errors'
import type { AuthStatus } from '../api/types'
import { ME_KEY } from '../app/queryClient'
import { safeNext } from '../lib/safeNext'

const PASSWORD_MAX = 256 // 서버 AuthEndpoints.PasswordMaxLength

export function LoginPage() {
  const [password, setPassword] = useState('')
  const [params] = useSearchParams()
  const navigate = useNavigate()
  const client = useQueryClient()

  const login = useMutation({
    mutationFn: (value: string) => auth.login(value),
    // 성공·실패와 무관하게 입력을 지운다: 비밀번호를 필요 이상으로 메모리(React 상태)에 두지 않는다.
    onSettled: () => setPassword(''),
    onSuccess: () => {
      client.setQueryData<AuthStatus>(ME_KEY, { authenticated: true })
      void navigate(safeNext(params.get('next')), { replace: true })
    },
  })

  const message = login.error instanceof ApiError && login.error.status === 401
    ? '비밀번호가 맞지 않습니다.'
    : login.error ? describeError(login.error) : null

  const submit = (event: FormEvent) => {
    event.preventDefault()
    if (password.length === 0 || login.isPending) return
    login.mutate(password)
  }

  return (
    <main className="mx-auto mt-24 max-w-sm p-4">
      <h1 className="mb-4 text-xl font-bold">관리자 로그인</h1>
      <form onSubmit={submit} className="space-y-3">
        <label className="block text-sm">
          <span>비밀번호</span>
          <input type="password" name="password" autoComplete="current-password" autoFocus required maxLength={PASSWORD_MAX}
            value={password} onChange={e => setPassword(e.target.value)} className="mt-1 w-full rounded border p-2" />
        </label>
        {message && <p role="alert" className="text-sm text-red-700">{message}</p>}
        <button type="submit" disabled={login.isPending} className="w-full rounded bg-black p-2 text-white disabled:opacity-50">
          {login.isPending ? '확인 중…' : '로그인'}
        </button>
      </form>
    </main>
  )
}
