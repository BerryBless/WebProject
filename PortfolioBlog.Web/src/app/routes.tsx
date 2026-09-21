import { Link, type RouteObject } from 'react-router'
import { LoginPage } from '../auth/LoginPage'
import { RequireAuth } from '../auth/RequireAuth'
import { Layout } from '../components/Layout'
import { Loading } from '../components/notices'
import { RouteError } from '../components/RouteError'
import { AttachmentsPage } from '../pages/AttachmentsPage'
import { PostsPage } from '../pages/PostsPage'
import { SeriesPage } from '../pages/SeriesPage'
import { TagsPage } from '../pages/TagsPage'

// 편집 화면만 지연 로딩한다: CodeMirror가 번들의 대부분이다(실측: 전부 한 덩어리면 953KB). 로그인·목록은 가볍게 뜬다.
const editor = () => import('../pages/PostEditorPage')

// 경로 없는(pathless) 루트 라우트로 전부를 감싼다. 경로 매칭은 그대로이고, 아래 어디서 던져진 오류든 여기서 받는다
// — 없으면 react-router의 기본 화면이 원시 오류와 스택을 그리고 돌아갈 길을 주지 않는다.
// hydrateFallbackElement는 루트 라우트의 것만 쓰인다(다른 곳에 두면 react-router가 콘솔 경고를 남긴다).
export const routes: RouteObject[] = [{
  errorElement: <RouteError />,
  hydrateFallbackElement: <Loading />,
  children: [
    { path: '/login', element: <LoginPage /> },
    {
      element: <RequireAuth />,
      children: [{
        element: <Layout />,
        children: [
          { path: '/', element: <PostsPage /> },
          { path: '/posts/new', lazy: editor },
          { path: '/posts/:id', lazy: editor },
          { path: '/series', element: <SeriesPage /> },
          { path: '/tags', element: <TagsPage /> },
          { path: '/attachments', element: <AttachmentsPage /> },
        ],
      }],
    },
    { path: '*', element: <main className="p-8 text-sm">없는 화면입니다. <Link to="/" className="underline">글 목록으로</Link></main> },
  ],
}]
