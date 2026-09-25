import { Route } from 'react-router';
export const routes = [{ path: '/editor', element: 'Editor' }, { path: '/editor/:id', element: 'Editor' }];
export function App() { return <Route path="/login" />; }
