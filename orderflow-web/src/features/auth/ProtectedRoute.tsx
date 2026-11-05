import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { LoadingState } from '../../components/PageStates';
import { useAuth } from './AuthProvider';
import type { Role } from './session';

// This guard only shapes navigation. The API enforces each permission on its own.
export function ProtectedRoute({ roles }: { roles?: Role[] }) {
  const { ready, current, hasRole } = useAuth();
  const location = useLocation();
  if (!ready) return <LoadingState label="Restoring your session" />;
  if (!current) return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />;
  if (roles && !hasRole(...roles)) return <Navigate to="/forbidden" replace />;
  return <Outlet />;
}
