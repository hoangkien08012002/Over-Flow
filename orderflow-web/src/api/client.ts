import axios, { type AxiosError, type InternalAxiosRequestConfig } from 'axios';
import { session } from '../features/auth/session';
import type { TokenResponse } from '../types/api';
import { toApiError } from './errors';

const baseURL = import.meta.env.VITE_API_BASE_URL || '/api/v1';
// The API sets the refresh token as an HttpOnly cookie when it sees this header, and ignores the cookie without it.
export const refreshTransportHeaders = { 'X-Refresh-Token-Transport': 'cookie' };

export const api = axios.create({ baseURL, withCredentials: true, headers: { 'Content-Type': 'application/json' } });
export const authApi = axios.create({ baseURL, withCredentials: true, headers: { 'Content-Type': 'application/json', ...refreshTransportHeaders } });

let refreshing: Promise<string> | null = null;

async function rotate(): Promise<string> {
  const { data } = await authApi.post<TokenResponse>('/auth/refresh', {});
  session.set(data.accessToken);
  return data.accessToken;
}

// Refresh tokens rotate and a reused one revokes the whole family, so a second concurrent refresh would sign the user out.
// One promise covers this tab, and a Web Lock covers other tabs: the next holder sends the cookie the previous one just received.
export function refreshAccessToken(): Promise<string> {
  refreshing ??= (navigator.locks ? navigator.locks.request('orderflow-refresh', rotate) : rotate()).finally(() => {
    refreshing = null;
  });
  return refreshing;
}

export async function login(email: string, password: string) {
  try {
    const { data } = await authApi.post<TokenResponse>('/auth/login', { email, password });
    session.set(data.accessToken);
  } catch (error) {
    throw toApiError(error);
  }
}

export async function logout() {
  try {
    await authApi.post('/auth/logout', {});
  } finally {
    session.clear();
  }
}

export async function register(body: { email: string; password: string; firstName: string; lastName: string }) {
  try {
    await authApi.post('/auth/register', body);
  } catch (error) {
    throw toApiError(error);
  }
}

api.interceptors.request.use((config) => {
  const token = session.token();
  if (token) config.headers.Authorization = `Bearer ${token}`;
  return config;
});

api.interceptors.response.use(undefined, async (error: AxiosError) => {
  const request = error.config as (InternalAxiosRequestConfig & { retried?: boolean }) | undefined;
  if (error.response?.status !== 401 || !request || request.retried || !session.token()) throw toApiError(error);
  request.retried = true;
  try {
    const token = await refreshAccessToken();
    request.headers.Authorization = `Bearer ${token}`;
  } catch {
    // The session is over. Clearing it makes protected routes send the user to sign in.
    session.clear();
    throw toApiError(error);
  }
  try {
    return await api.request(request);
  } catch (retryError) {
    throw toApiError(retryError);
  }
});
