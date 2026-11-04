import { createContext, useContext, useEffect, useState, useSyncExternalStore, type ReactNode } from 'react';
import { refreshAccessToken } from '../../api/client';
import { session, type Role, type Session } from './session';

interface AuthState {
  // False until the first refresh attempt ends. Routes wait for it, so a reload does not flash the sign-in page.
  ready: boolean;
  current: Session | null;
  hasRole: (...roles: Role[]) => boolean;
}

const AuthContext = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(false);
  const current = useSyncExternalStore(session.subscribe, session.get);

  useEffect(() => {
    let active = true;
    refreshAccessToken()
      .catch(() => session.clear())
      .finally(() => {
        if (active) setReady(true);
      });
    return () => {
      active = false;
    };
  }, []);

  const hasRole = (...roles: Role[]) => roles.some((role) => current?.user.roles.includes(role));
  return <AuthContext.Provider value={{ ready, current, hasRole }}>{children}</AuthContext.Provider>;
}

export function useAuth() {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth must be used inside AuthProvider.');
  return value;
}
