import { Link, type RouteObject } from 'react-router'
import { LoginPage } from '../auth/LoginPage'
import { RequireAuth } from '../auth/RequireAuth'
import { Layout } from '../components/Layout'
import { PostsPage } from '../pages/PostsPage'
import { SeriesPage } from '../pages/SeriesPage'
import { TagsPage } from '../pages/TagsPage'


export const routes: RouteObject[] = [
  { path: '/login', element: <LoginPage /> },
  {
    element: <RequireAuth />,
    children: [{
      element: <Layout />,
      children: [
        { path: '/', element: <PostsPage /> },
        { path: '/series', element: <SeriesPage /> },
        { path: '/tags', element: <TagsPage /> },
      ],
    }],
  },
  { path: '*', element: <main className="p-8 text-sm">없는 화면입니다. <Link to="/" className="underline">글 목록으로</Link></main> },
]
