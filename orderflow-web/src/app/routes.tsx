import type { RouteObject } from 'react-router-dom';
import { AuditLogPage } from '../features/admin/AuditLogPage';
import { LoginPage } from '../features/auth/LoginPage';
import { ProtectedRoute } from '../features/auth/ProtectedRoute';
import { RegisterPage } from '../features/auth/RegisterPage';
import { ProductListPage } from '../features/catalog/ProductListPage';
import { OrdersPage } from '../features/orders/OrdersPage';
import { AppLayout } from '../layouts/AppLayout';
import { ForbiddenPage, NotFoundPage } from '../pages/StatusPages';

export const routes: RouteObject[] = [
  {
    element: <AppLayout />,
    children: [
      { path: '/', element: <ProductListPage /> },
      { path: '/login', element: <LoginPage /> },
      { path: '/register', element: <RegisterPage /> },
      { path: '/forbidden', element: <ForbiddenPage /> },
      { element: <ProtectedRoute roles={['Customer']} />, children: [{ path: '/orders', element: <OrdersPage /> }] },
      { element: <ProtectedRoute roles={['Admin']} />, children: [{ path: '/admin/audit-logs', element: <AuditLogPage /> }] },
      { path: '*', element: <NotFoundPage /> }
    ]
  }
];
