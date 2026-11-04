import { isAxiosError } from 'axios';

// Every failure the UI shows goes through this type, so the correlation ID support needs is always available.
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    title: string,
    readonly correlationId: string | null,
    readonly fieldErrors: Record<string, string[]> = {}
  ) {
    super(title);
    this.name = 'ApiError';
  }
}

interface ProblemBody {
  title?: string;
  code?: string;
  correlationId?: string;
  errors?: Record<string, string[]>;
}

const friendlyMessages: Record<string, string> = {
  AUTH_INVALID_CREDENTIALS: 'The email or password is incorrect.',
  ACCOUNT_LOCKED: 'This account is temporarily locked. Try again later.',
  EMAIL_ALREADY_EXISTS: 'An account with this email already exists.',
  RATE_LIMITED: 'Too many requests. Wait a moment and try again.',
  NETWORK_ERROR: 'The server could not be reached. Check your connection and try again.',
  INTERNAL_ERROR: 'Something went wrong on our side.'
};

export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error;
  if (!isAxiosError(error)) return new ApiError(0, 'UNEXPECTED', 'Something unexpected happened.', null);
  if (!error.response) return new ApiError(0, 'NETWORK_ERROR', friendlyMessages.NETWORK_ERROR, null);
  const body = (typeof error.response.data === 'object' && error.response.data !== null ? error.response.data : {}) as ProblemBody;
  const header = error.response.headers?.['x-correlation-id'] as string | undefined;
  const code = body.code ?? `HTTP_${error.response.status}`;
  return new ApiError(error.response.status, code, friendlyMessages[code] ?? body.title ?? 'The request failed.', body.correlationId || header || null, body.errors ?? {});
}
