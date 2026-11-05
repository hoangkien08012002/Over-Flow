// The access token lives only in this module's memory. A reload loses it and the refresh cookie restores it, so script storage never holds a credential.
export type Role = 'Customer' | 'Support' | 'InventoryManager' | 'Admin';

export interface SessionUser {
  id: string;
  email: string;
  roles: Role[];
}

export interface Session {
  accessToken: string;
  user: SessionUser;
}

const roleClaim = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';

// For display and menu decisions only. The API verifies the signature and enforces every permission.
export function readUser(accessToken: string): SessionUser {
  const payload = accessToken.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
  const bytes = Uint8Array.from(atob(payload.padEnd(payload.length + ((4 - (payload.length % 4)) % 4), '=')), (c) => c.charCodeAt(0));
  const claims = JSON.parse(new TextDecoder().decode(bytes)) as Record<string, unknown>;
  const roles = claims[roleClaim] ?? claims.role ?? [];
  return { id: String(claims.sub), email: String(claims.email), roles: (Array.isArray(roles) ? roles : [roles]) as Role[] };
}

let current: Session | null = null;
const listeners = new Set<() => void>();

export const session = {
  get: () => current,
  token: () => current?.accessToken ?? null,
  set(accessToken: string) {
    current = { accessToken, user: readUser(accessToken) };
    listeners.forEach((listener) => listener());
  },
  clear() {
    if (current === null) return;
    current = null;
    listeners.forEach((listener) => listener());
  },
  subscribe(listener: () => void) {
    listeners.add(listener);
    return () => listeners.delete(listener);
  }
};
