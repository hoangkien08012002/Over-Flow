import type { ApiError } from '../../api/errors';
import type { CheckoutRequest } from '../../types/api';

// True when the first request may have committed, so only a retry with the same key is safe. The server rolls back its key record on any
// other refusal, which makes those definite failures: fixing the form and submitting is a new attempt.
export const outcomeIsUnknown = (error: ApiError) =>
  error.status === 0 || error.status >= 500 || error.code === 'REQUEST_IN_PROGRESS' || error.code === 'CONCURRENCY_CONFLICT';

// One key belongs to one attempt: the same request sent again after an unknown outcome. Any change to the request starts a new attempt.
// It is held in memory only, because the fingerprint contains a delivery address. After a reload the emptied cart stops a repeat instead.
export function createCheckoutAttempt(newKey: () => string = () => crypto.randomUUID()) {
  let current: { fingerprint: string; key: string } | null = null;
  return {
    keyFor(request: CheckoutRequest) {
      const fingerprint = JSON.stringify(request);
      if (current?.fingerprint !== fingerprint) current = { fingerprint, key: newKey() };
      return current.key;
    },
    settled(error?: ApiError) {
      if (!error || !outcomeIsUnknown(error)) current = null;
    }
  };
}
