import { Link, type RouteObject } from 'react-router'
import { LoginPage } from '../auth/LoginPage'
import { RequireAuth } from '../auth/RequireAuth'
import { Layout } from '../components/Layout'
import { AttachmentsPage } from '../pages/AttachmentsPage'
import { PostsPage } from '../pages/PostsPage'
import { SeriesPage } from '../pages/SeriesPage'
import { TagsPage } from '../pages/TagsPage'

// 편집 화면만 지연 로딩한다: CodeMirror가 번들의 대부분이다(실측: 전부 한 덩어리면 953KB). 로그인·목록은 가볍게 뜬다.
const editor = () => import('../pages/PostEditorPage')

export const routes: RouteObject[] = [
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
]
