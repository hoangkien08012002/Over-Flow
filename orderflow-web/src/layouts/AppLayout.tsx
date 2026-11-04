import { AppBar, Box, Button, Container, Toolbar, Typography } from '@mui/material';
import { useQueryClient } from '@tanstack/react-query';
import { Link as RouterLink, Outlet } from 'react-router-dom';
import { logout } from '../api/client';
import { useAuth } from '../features/auth/AuthProvider';
import type { Role } from '../features/auth/session';

const menu: { to: string; label: string; roles?: Role[] }[] = [
  { to: '/', label: 'Products' },
  { to: '/orders', label: 'My orders', roles: ['Customer'] },
  { to: '/admin/audit-logs', label: 'Audit log', roles: ['Admin'] }
];

export function AppLayout() {
  const { current, hasRole } = useAuth();
  const queryClient = useQueryClient();
  const signOut = async () => {
    await logout().catch(() => undefined);
    // One account's cached data must not be shown to the next person on this browser.
    queryClient.clear();
  };
  return (
    <>
      <AppBar position="static">
        <Toolbar sx={{ gap: 1, flexWrap: 'wrap' }}>
          <Typography variant="h6" component="span" sx={{ mr: 2 }}>OrderFlow</Typography>
          <Box component="nav" aria-label="Main" sx={{ display: 'flex', gap: 1, flexGrow: 1, flexWrap: 'wrap' }}>
            {menu.filter((item) => !item.roles || hasRole(...item.roles)).map((item) => (
              <Button key={item.to} color="inherit" component={RouterLink} to={item.to}>{item.label}</Button>
            ))}
          </Box>
          {current ? (
            <>
              <Typography variant="body2" data-testid="signed-in-as">{current.user.email}</Typography>
              <Button color="inherit" onClick={signOut}>Sign out</Button>
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
