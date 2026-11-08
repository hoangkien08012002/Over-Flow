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
  INTERNAL_ERROR: 'Something went wrong on our side.',
  VALIDATION_FAILED: 'Some of the details are not valid. Check them and try again.',
  FORBIDDEN: 'You do not have access to this.',
  CONCURRENCY_CONFLICT: 'Someone else changed this at the same time. Reload it and try again.',
  INVALID_STATE_TRANSITION: 'This action is not available in the current status.',
  INVENTORY_BELOW_ZERO: 'This adjustment would take stock below zero or below what is reserved.',
  REQUEST_IN_PROGRESS: 'This order is still being submitted. Try again in a moment.',
  IDEMPOTENCY_KEY_REUSED: 'This attempt was already used for a different order. Submit it again.',
  ORDER_NOT_FOUND: 'This order could not be found.',
  RESOURCE_NOT_FOUND: 'This could not be found.',
  SKU_ALREADY_EXISTS: 'Another product already uses this SKU.',
  SLUG_ALREADY_EXISTS: 'Another category already uses this slug.',
  COUPON_CODE_ALREADY_EXISTS: 'Another coupon already uses this code.',
  SHIPPING_CODE_ALREADY_EXISTS: 'Another shipping method already uses this code.',
  RETURN_NOT_ELIGIBLE: 'Only a delivered order can be returned.',
  RETURN_WINDOW_CLOSED: 'The return window for this order has closed.',
  RETURN_ALREADY_EXISTS: 'A return has already been requested for this order.',
  REVIEW_NOT_ELIGIBLE: 'You can review this item once it has been delivered.',
  REVIEW_ALREADY_EXISTS: 'You have already reviewed this item.'
};

// Splits a ProblemDetails response into the errors a form can show beside its fields, and whether anything is left for a page-level message.
// The API names fields in camel case and may nest them, as in deliveryAddress.line1, so the last segment is compared without regard to case.
export function mapFieldErrors(error: ApiError, fields: readonly string[]) {
  const mapped: Record<string, string> = {};
  let unmapped = false;
  for (const [key, messages] of Object.entries(error.fieldErrors)) {
    const leaf = key.split('.').pop()!.toLowerCase();
    const field = fields.find((candidate) => candidate.toLowerCase() === leaf);
    if (field && messages[0]) mapped[field] = messages[0];
    else unmapped = true;
  }
  return { mapped, showGlobal: unmapped || Object.keys(mapped).length === 0 };
}

export function toApiError(error: unknown): ApiError {
  if (error instanceof ApiError) return error;
  if (!isAxiosError(error)) return new ApiError(0, 'UNEXPECTED', 'Something unexpected happened.', null);
  if (!error.response) return new ApiError(0, 'NETWORK_ERROR', friendlyMessages.NETWORK_ERROR, null);
  const body = (typeof error.response.data === 'object' && error.response.data !== null ? error.response.data : {}) as ProblemBody;
  const header = error.response.headers?.['x-correlation-id'] as string | undefined;
  const code = body.code ?? `HTTP_${error.response.status}`;
  return new ApiError(error.response.status, code, friendlyMessages[code] ?? body.title ?? 'The request failed.', body.correlationId || header || null, body.errors ?? {});
}
