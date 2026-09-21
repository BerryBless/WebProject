import { useState } from 'react'
import { QueryClientProvider } from '@tanstack/react-query'
import { createBrowserRouter, RouterProvider } from 'react-router'
import { createQueryClient } from './app/queryClient'
import { routes } from './app/routes'

export default function App() {
  const [client] = useState(createQueryClient)
  const [router] = useState(() => createBrowserRouter(routes))
  return <QueryClientProvider client={client}><RouterProvider router={router} /></QueryClientProvider>
}
