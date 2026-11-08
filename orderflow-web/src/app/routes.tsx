import type { RouteObject } from 'react-router-dom';
import { AuditLogPage } from '../features/admin/AuditLogPage';
import { CategoriesAdminPage } from '../features/admin/CategoriesAdminPage';
import { CouponsAdminPage } from '../features/admin/CouponsAdminPage';
import { InventoryDetailPage, InventoryListPage } from '../features/admin/InventoryPages';
import { OperationsPage } from '../features/admin/OperationsPage';
import { OrderAdminDetailPage, OrdersAdminPage } from '../features/admin/OrdersAdminPages';
import { ProductsAdminPage } from '../features/admin/ProductsAdminPage';
import { ReturnsAdminPage } from '../features/admin/ReturnsAdminPage';
import { ReviewsAdminPage } from '../features/admin/ReviewsAdminPage';
import { ShipmentsPage } from '../features/admin/ShipmentsPage';
import { ShippingMethodsAdminPage } from '../features/admin/ShippingMethodsAdminPage';
import { LoginPage } from '../features/auth/LoginPage';
import { ProtectedRoute } from '../features/auth/ProtectedRoute';
import { RegisterPage } from '../features/auth/RegisterPage';
import type { Role } from '../features/auth/session';
import { CartPage } from '../features/cart/CartPage';
import { CheckoutPage } from '../features/cart/CheckoutPage';
import { ProductDetailPage } from '../features/catalog/ProductDetailPage';
import { ProductListPage } from '../features/catalog/ProductListPage';
import { OrderDetailPage } from '../features/orders/OrderDetailPage';
import { OrdersPage } from '../features/orders/OrdersPage';
import { WishlistPage } from '../features/wishlist/WishlistPage';
import { AppLayout } from '../layouts/AppLayout';
import { ForbiddenPage, NotFoundPage } from '../pages/StatusPages';

// The role lists mirror the API policies, so a menu never offers a page the API would refuse.
const allowed = (roles: Role[], children: RouteObject[]): RouteObject => ({ element: <ProtectedRoute roles={roles} />, children });

export const routes: RouteObject[] = [
  {
    element: <AppLayout />,
    children: [
      { path: '/', element: <ProductListPage /> },
      { path: '/products/:id', element: <ProductDetailPage /> },
      { path: '/login', element: <LoginPage /> },
      { path: '/register', element: <RegisterPage /> },
      { path: '/forbidden', element: <ForbiddenPage /> },
      allowed(['Customer'], [
        { path: '/cart', element: <CartPage /> },
        { path: '/checkout', element: <CheckoutPage /> },
        { path: '/orders', element: <OrdersPage /> },
        { path: '/orders/:id', element: <OrderDetailPage /> },
        { path: '/orders/:id/accepted', element: <OrderDetailPage accepted /> },
        { path: '/wishlist', element: <WishlistPage /> }
      ]),
      allowed(['Admin'], [
        { path: '/admin/products', element: <ProductsAdminPage /> },
        { path: '/admin/categories', element: <CategoriesAdminPage /> },
        { path: '/admin/coupons', element: <CouponsAdminPage /> },
        { path: '/admin/shipping-methods', element: <ShippingMethodsAdminPage /> },
        { path: '/admin/reviews', element: <ReviewsAdminPage /> },
        { path: '/admin/operations', element: <OperationsPage /> },
        { path: '/admin/audit-logs', element: <AuditLogPage /> }
      ]),
      allowed(['InventoryManager'], [
        { path: '/admin/inventory', element: <InventoryListPage /> },
        { path: '/admin/inventory/:productId', element: <InventoryDetailPage /> }
      ]),
      allowed(['InventoryManager', 'Admin'], [{ path: '/admin/shipments', element: <ShipmentsPage /> }]),
      allowed(['Support', 'Admin'], [
        { path: '/admin/orders', element: <OrdersAdminPage /> },
        { path: '/admin/orders/:id', element: <OrderAdminDetailPage /> }
      ]),
      allowed(['Support', 'InventoryManager', 'Admin'], [{ path: '/admin/returns', element: <ReturnsAdminPage /> }]),
      { path: '*', element: <NotFoundPage /> }
    ]
  }
];
