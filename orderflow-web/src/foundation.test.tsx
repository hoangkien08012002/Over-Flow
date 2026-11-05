import { render, screen, waitFor } from '@testing-library/react';
import { AxiosError, AxiosHeaders, type AxiosAdapter, type InternalAxiosRequestConfig } from 'axios';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it } from 'vitest';
import { api, authApi, refreshAccessToken } from './api/client';
import { ApiError, toApiError } from './api/errors';
import { ErrorState } from './components/PageStates';
import { AuthProvider } from './features/auth/AuthProvider';
import { safeReturnPath } from './features/auth/LoginPage';
import { ProtectedRoute } from './features/auth/ProtectedRoute';
import { readUser, session } from './features/auth/session';

const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
const token = (claims: object = {}) => `${encode({ alg: 'none' })}.${encode({ sub: 'u1', email: 'a@example.invalid', ...claims })}.signature`;
const roleClaim = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const blank = { headers: new AxiosHeaders() } as InternalAxiosRequestConfig;

function failure(config: InternalAxiosRequestConfig, status: number, data: unknown = {}, headers: Record<string, string> = {}) {
  return new AxiosError('failed', undefined, config, null, { status, statusText: '', data, headers: new AxiosHeaders(headers), config });
}
const ok = (config: InternalAxiosRequestConfig, data: unknown = {}) => ({ status: 200, statusText: 'OK', data, headers: {}, config });

beforeEach(() => session.clear());

describe('error mapping', () => {
  it('keeps the correlation ID from the problem body', () => {
    const error = toApiError(failure(blank, 409, { code: 'INVALID_STATE_TRANSITION', title: 'Bad transition', correlationId: 'corr-1' }));
    expect(error).toMatchObject({ status: 409, code: 'INVALID_STATE_TRANSITION', correlationId: 'corr-1', message: 'Bad transition' });
  });

  it('falls back to the response header when the body has none', () => {
    expect(toApiError(failure(blank, 502, '<html>', { 'x-correlation-id': 'corr-2' })).correlationId).toBe('corr-2');
  });

  it('reports a network failure without a correlation ID', () => {
    expect(toApiError(new AxiosError('Network Error', 'ERR_NETWORK'))).toMatchObject({ code: 'NETWORK_ERROR', correlationId: null });
  });

  it('shows the correlation ID to the user', () => {
    render(<ErrorState error={new ApiError(500, 'INTERNAL_ERROR', 'Something went wrong', 'corr-3')} />);
    expect(screen.getByTestId('correlation-id')).toHaveTextContent('corr-3');
  });
});

describe('session', () => {
  it('reads roles from the long claim type, as one value or many', () => {
    expect(readUser(token({ [roleClaim]: 'Admin' })).roles).toEqual(['Admin']);
    expect(readUser(token({ [roleClaim]: ['Admin', 'Support'] })).roles).toEqual(['Admin', 'Support']);
    expect(readUser(token()).roles).toEqual([]);
  });

  it('never writes a token to browser storage', () => {
    session.set(token());
    expect(localStorage.length).toBe(0);
    expect(sessionStorage.length).toBe(0);
  });

  it('follows only return paths inside the app', () => {
    expect(safeReturnPath('/orders?page=2')).toBe('/orders?page=2');
    expect(safeReturnPath('//evil.example')).toBe('/');
    expect(safeReturnPath('https://evil.example')).toBe('/');
    expect(safeReturnPath(undefined)).toBe('/');
  });
});

describe('refresh flow', () => {
  const fresh = token({ jti: 'new' });
  // The API accepts only the fresh token, so anything else answers 401 like an expired one.
  const protectedApi: AxiosAdapter = async (config) => {
    if (String(config.headers.Authorization) === `Bearer ${fresh}`) return ok(config, { fine: true });
    throw failure(config, 401, { code: 'AUTHENTICATION_REQUIRED', correlationId: 'corr-401' });
  };

  it('shares one refresh between concurrent 401s and retries each request', async () => {
    let refreshes = 0;
    authApi.defaults.adapter = async (config) => {
      refreshes++;
      await new Promise((resolve) => setTimeout(resolve, 5));
      return ok(config, { accessToken: fresh });
    };
    api.defaults.adapter = protectedApi;
    session.set(token({ jti: 'old' }));
    const responses = await Promise.all([api.get('/a'), api.get('/b'), api.get('/c')]);
    expect(responses.map((x) => x.data.fine)).toEqual([true, true, true]);
    expect(refreshes).toBe(1);
    expect(session.token()).toBe(fresh);
  });

  it('ends the session when the refresh is refused, and reports the original failure', async () => {
    authApi.defaults.adapter = async (config) => { throw failure(config, 401, { code: 'REFRESH_TOKEN_REVOKED' }); };
    api.defaults.adapter = protectedApi;
    session.set(token({ jti: 'old' }));
    await expect(api.get('/a')).rejects.toMatchObject({ status: 401, correlationId: 'corr-401' });
    expect(session.get()).toBeNull();
  });

  it('does not attempt a refresh for an anonymous request', async () => {
    let refreshes = 0;
    authApi.defaults.adapter = async (config) => { refreshes++; return ok(config, { accessToken: fresh }); };
    api.defaults.adapter = protectedApi;
    await expect(api.get('/a')).rejects.toBeInstanceOf(ApiError);
    expect(refreshes).toBe(0);
  });

  it('sends the cookie transport header with each refresh', async () => {
    let header: unknown;
    authApi.defaults.adapter = async (config) => { header = config.headers['X-Refresh-Token-Transport']; return ok(config, { accessToken: fresh }); };
    await refreshAccessToken();
    expect(header).toBe('cookie');
  });
});

describe('protected routes', () => {
  function renderAt(roles?: ('Admin' | 'Customer')[]) {
    render(
      <AuthProvider>
        <MemoryRouter initialEntries={['/private']}>
          <Routes>
            <Route path="/login" element={<p>login page</p>} />
            <Route path="/forbidden" element={<p>forbidden page</p>} />
            <Route element={<ProtectedRoute roles={roles} />}>
              <Route path="/private" element={<p>private page</p>} />
            </Route>
          </Routes>
        </MemoryRouter>
      </AuthProvider>
    );
  }

  it('sends an anonymous visitor to sign in once the refresh attempt fails', async () => {
    authApi.defaults.adapter = async (config) => { throw failure(config, 401); };
    renderAt();
    expect(screen.getByRole('status')).toHaveTextContent('Restoring your session');
    await waitFor(() => expect(screen.getByText('login page')).toBeInTheDocument());
  });

  it('restores the session from the refresh cookie after a reload', async () => {
    authApi.defaults.adapter = async (config) => ok(config, { accessToken: token({ [roleClaim]: 'Customer' }) });
    renderAt(['Customer']);
    await waitFor(() => expect(screen.getByText('private page')).toBeInTheDocument());
  });

  it('refuses a signed-in user without the required role', async () => {
    authApi.defaults.adapter = async (config) => ok(config, { accessToken: token({ [roleClaim]: 'Customer' }) });
    renderAt(['Admin']);
    await waitFor(() => expect(screen.getByText('forbidden page')).toBeInTheDocument());
  });
});
