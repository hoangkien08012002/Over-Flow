import { AppBar, Box, Button, Container, Menu, MenuItem, Toolbar, Typography } from '@mui/material';
import { useState } from 'react';
import { Link as RouterLink, Outlet } from 'react-router-dom';
import { logout } from '../api/client';
import { useAuth } from '../features/auth/AuthProvider';
import type { Role } from '../features/auth/session';
import { useCart } from '../features/cart/useCart';

interface MenuEntry { to: string; label: string; roles?: Role[] }

const customerMenu: MenuEntry[] = [
  { to: '/', label: 'Products' },
  { to: '/orders', label: 'My orders', roles: ['Customer'] },
  { to: '/wishlist', label: 'Wishlist', roles: ['Customer'] }
];

const staffMenu: MenuEntry[] = [
  { to: '/admin/products', label: 'Products', roles: ['Admin'] },
  { to: '/admin/categories', label: 'Categories', roles: ['Admin'] },
  { to: '/admin/inventory', label: 'Inventory', roles: ['InventoryManager'] },
  { to: '/admin/orders', label: 'Orders', roles: ['Support', 'Admin'] },
  { to: '/admin/shipments', label: 'Shipments', roles: ['InventoryManager', 'Admin'] },
  { to: '/admin/returns', label: 'Returns', roles: ['Support', 'InventoryManager', 'Admin'] },
  { to: '/admin/coupons', label: 'Coupons', roles: ['Admin'] },
  { to: '/admin/shipping-methods', label: 'Shipping methods', roles: ['Admin'] },
  { to: '/admin/reviews', label: 'Reviews', roles: ['Admin'] },
  { to: '/admin/operations', label: 'Operations', roles: ['Admin'] },
  { to: '/admin/audit-logs', label: 'Audit log', roles: ['Admin'] }
];

function StaffMenu({ entries }: { entries: MenuEntry[] }) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  return (
    <>
      <Button color="inherit" aria-haspopup="menu" onClick={(event) => setAnchor(event.currentTarget)}>Staff</Button>
      <Menu anchorEl={anchor} open={anchor !== null} onClose={() => setAnchor(null)}>
        {entries.map((entry) => <MenuItem key={entry.to} component={RouterLink} to={entry.to} onClick={() => setAnchor(null)}>{entry.label}</MenuItem>)}
      </Menu>
    </>
  );
}

export function AppLayout() {
  const { current, hasRole } = useAuth();
  const cart = useCart();
  const isVisible = (entry: MenuEntry) => !entry.roles || hasRole(...entry.roles);
  const staffEntries = staffMenu.filter(isVisible);
  const cartCount = cart.data?.items.reduce((sum, line) => sum + line.quantity, 0) ?? 0;
  return (
    <>
      <AppBar position="static">
        <Toolbar sx={{ gap: 1, flexWrap: 'wrap' }}>
          <Typography variant="h6" component="span" sx={{ mr: 2 }}>OrderFlow</Typography>
          <Box component="nav" aria-label="Main" sx={{ display: 'flex', gap: 1, flexGrow: 1, flexWrap: 'wrap' }}>
            {customerMenu.filter(isVisible).map((item) => (
              <Button key={item.to} color="inherit" component={RouterLink} to={item.to}>{item.label}</Button>
            ))}
            {hasRole('Customer') && <Button color="inherit" component={RouterLink} to="/cart" data-testid="cart-link">Cart ({cartCount})</Button>}
            {staffEntries.length > 0 && <StaffMenu entries={staffEntries} />}
          </Box>
          {current ? (
            <>
              <Typography variant="body2" data-testid="signed-in-as">{current.user.email}</Typography>
              <Button color="inherit" onClick={() => logout().catch(() => undefined)}>Sign out</Button>
            </>
          ) : (
            <Button color="inherit" component={RouterLink} to="/login">Sign in</Button>
          )}
        </Toolbar>
      </AppBar>
      <Container component="main" sx={{ py: 3 }}>
        <Outlet />
      </Container>
    </>
  );
}
