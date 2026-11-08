import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { AxiosError, AxiosHeaders, type InternalAxiosRequestConfig } from 'axios';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api, authApi } from './api/client';
import { ApiError, mapFieldErrors, toApiError } from './api/errors';
import { routes } from './app/routes';
import { AuthProvider } from './features/auth/AuthProvider';
import { safeReturnPath } from './features/auth/LoginPage';
import { session } from './features/auth/session';
import { checkoutSchema, CheckoutPage } from './features/cart/CheckoutPage';
import { createCheckoutAttempt, outcomeIsUnknown } from './features/cart/checkoutAttempt';
import { OrderDetailPage } from './features/orders/OrderDetailPage';
import { orderPollInterval, POLL_MS } from './features/orders/orderStatus';
import { AppLayout } from './layouts/AppLayout';
import type { CheckoutRequest } from './types/api';

const encode = (value: object) => btoa(JSON.stringify(value)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
const roleClaim = 'http://schemas.microsoft.com/ws/2008/06/identity/claims/role';
const token = (...roles: string[]) => `${encode({ alg: 'none' })}.${encode({ sub: 'u1', email: 'a@example.invalid', [roleClaim]: roles })}.signature`;
const respond = (config: InternalAxiosRequestConfig, data: unknown, status = 200) => ({ status, statusText: '', data, headers: {}, config });
const failure = (config: InternalAxiosRequestConfig, status: number, data: unknown) =>
  new AxiosError('failed', undefined, config, null, { status, statusText: '', data, headers: new AxiosHeaders(), config });

const request: CheckoutRequest = {
  cartId: 'cart', shippingMethodId: 'ship', paymentMethod: 'COD', couponCode: null,
  deliveryAddress: { recipientName: 'A', phone: '1', line1: 'L', ward: 'W', district: 'D', province: 'P', countryCode: 'VN' }
};

function renderWith(ui: React.ReactNode, path: string, routePath: string, roles: string[]) {
  authApi.defaults.adapter = async (config) => respond(config, { accessToken: token(...roles) });
  session.set(token(...roles));
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <AuthProvider>
        <MemoryRouter initialEntries={[path]}>
          <Routes>
            <Route path={routePath} element={ui} />
            <Route path="/cart" element={<p>cart page</p>} />
            <Route path="/orders/:id/accepted" element={<p>accepted page</p>} />
          </Routes>
        </MemoryRouter>
      </AuthProvider>
    </QueryClientProvider>
  );
}

beforeEach(() => session.clear());

describe('checkout idempotency attempt', () => {
  const keys = () => { let n = 0; return () => `key-${++n}`; };

  it('reuses the key only when the same request is retried after an unknown outcome', () => {
    const attempt = createCheckoutAttempt(keys());
    expect(attempt.keyFor(request)).toBe('key-1');
    attempt.settled(new ApiError(0, 'NETWORK_ERROR', 'down', null));
    expect(attempt.keyFor(request)).toBe('key-1');
    attempt.settled(new ApiError(503, 'HTTP_503', 'down', 'c'));
    expect(attempt.keyFor(request)).toBe('key-1');
    attempt.settled(new ApiError(409, 'REQUEST_IN_PROGRESS', 'wait', 'c'));
    expect(attempt.keyFor(request)).toBe('key-1');
  });

  it('starts a new attempt when the request changes', () => {
    const attempt = createCheckoutAttempt(keys());
    attempt.keyFor(request);
    attempt.settled(new ApiError(0, 'NETWORK_ERROR', 'down', null));
    expect(attempt.keyFor({ ...request, couponCode: 'SAVE' })).toBe('key-2');
  });

  it('starts a new attempt after a definite refusal or a success', () => {
    const attempt = createCheckoutAttempt(keys());
    attempt.keyFor(request);
    attempt.settled(new ApiError(400, 'VALIDATION_FAILED', 'bad', 'c'));
    expect(attempt.keyFor(request)).toBe('key-2');
    attempt.settled();
    expect(attempt.keyFor(request)).toBe('key-3');
  });

  it('treats a conflict that should be retried as unknown', () => {
    expect(outcomeIsUnknown(new ApiError(409, 'CONCURRENCY_CONFLICT', 'retry', 'c'))).toBe(true);
    expect(outcomeIsUnknown(new ApiError(409, 'IDEMPOTENCY_KEY_REUSED', 'no', 'c'))).toBe(false);
  });
});

describe('checkout validation', () => {
  const valid = { recipientName: 'A', phone: '0900 000 000', line1: 'L', ward: 'W', district: 'D', province: 'P', countryCode: 'vn', shippingMethodId: 'x', paymentMethod: 'COD', couponCode: '' };

  it('normalizes a valid form', () => {
    expect(checkoutSchema.parse(valid).countryCode).toBe('VN');
  });

  it.each([
    ['recipientName', ''], ['recipientName', ' '], ['recipientName', 'x'.repeat(151)], ['phone', ''], ['phone', 'call me'], ['line1', ''], ['ward', ''], ['district', ''], ['province', ''],
    ['countryCode', 'VNM'], ['shippingMethodId', ''], ['paymentMethod', 'Cheque']
  ])('rejects %s = "%s"', (field, value) => {
    const result = checkoutSchema.safeParse({ ...valid, [field]: value });
    expect(result.success).toBe(false);
    expect(result.error?.issues[0].path).toEqual([field]);
  });
});

describe('problem details', () => {
  it('maps nested and camel case field names, and needs no global message when all are mapped', () => {
    const problem = new ApiError(400, 'VALIDATION_FAILED', 'bad', 'c', { couponCode: ['Nope'], 'deliveryAddress.Line1': ['Missing'] });
    expect(mapFieldErrors(problem, ['couponCode', 'line1'])).toEqual({ mapped: { couponCode: 'Nope', line1: 'Missing' }, showGlobal: false });
  });

  it('keeps a global message for a field the form does not have', () => {
    expect(mapFieldErrors(new ApiError(400, 'VALIDATION_FAILED', 'bad', 'c', { cartId: ['Empty'] }), ['couponCode'])).toEqual({ mapped: {}, showGlobal: true });
  });

  it('keeps a global message when the problem names no field', () => {
    expect(mapFieldErrors(new ApiError(409, 'CONCURRENCY_CONFLICT', 'bad', 'c'), ['couponCode']).showGlobal).toBe(true);
  });

  it('gives a business code a readable message instead of its title', () => {
    expect(toApiError(failure({ headers: new AxiosHeaders() } as InternalAxiosRequestConfig, 409, { code: 'RETURN_WINDOW_CLOSED', title: 'RETURN WINDOW CLOSED' })).message).toBe('The return window for this order has closed.');
  });
});

describe('return path', () => {
  it('refuses a backslash, which a browser reads as a slash', () => {
    expect(safeReturnPath('/\\evil.example')).toBe('/');
  });
});

describe('order status polling', () => {
  it('polls only while the Worker is about to move the order', () => {
    for (const status of ['PendingInventory', 'AwaitingPayment', 'Paid']) expect(orderPollInterval(status)).toBe(POLL_MS);
    for (const status of ['Processing', 'Shipped', 'Delivered', 'Refunded', 'Rejected', 'PaymentFailed', 'Cancelled']) expect(orderPollInterval(status)).toBe(false);
    expect(POLL_MS).toBeGreaterThanOrEqual(2000);
    expect(POLL_MS).toBeLessThanOrEqual(3000);
  });

  it('updates a pending order without a reload, then stops polling', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      const statuses = ['PendingInventory', 'AwaitingPayment', 'Processing'];
      let reads = 0;
      api.defaults.adapter = async (config) => {
        const url = String(config.url);
        if (url.endsWith('/shipment')) throw failure(config, 404, { code: 'RESOURCE_NOT_FOUND' });
        const status = statuses[Math.min(reads++, statuses.length - 1)];
        return respond(config, {
          order: { id: 'o1', orderNumber: 'OF-1', status, subtotal: 1, discountTotal: 0, shippingFee: 1, grandTotal: 2, currencyCode: 'VND', statusUrl: '' },
          items: [], history: [], deliveryAddress: null, paymentMethod: 'COD', shippingMethodName: 'Std'
        });
      };
      renderWith(<OrderDetailPage />, '/orders/o1', '/orders/:id', ['Customer']);
      await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('PendingInventory'));
      expect(screen.getByRole('button', { name: 'Cancel order' })).toBeInTheDocument();
      await vi.advanceTimersByTimeAsync(POLL_MS);
      await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('AwaitingPayment'));
      await vi.advanceTimersByTimeAsync(POLL_MS);
      await waitFor(() => expect(screen.getByTestId('status')).toHaveTextContent('Processing'));
      expect(screen.queryByRole('button', { name: 'Cancel order' })).not.toBeInTheDocument();
      const settled = reads;
      await vi.advanceTimersByTimeAsync(POLL_MS * 3);
      expect(reads).toBe(settled);
    } finally {
      vi.useRealTimers();
    }
  });
});

describe('checkout page', () => {
  const cart = { id: 'c1', subtotal: 100, items: [{ id: 'l1', productId: 'p1', quantity: 1, currencyCode: 'VND', productName: 'P', currentUnitPrice: 100, available: 5, unavailable: false, lineSubtotal: 100 }] };

  async function fillAndSubmit() {
    await screen.findByRole('heading', { name: 'Checkout' });
    for (const [label, value] of [['Recipient name', 'A'], ['Phone', '0900000000'], ['Street address', 'L'], ['Ward', 'W'], ['District', 'D'], ['Province', 'P']])
      fireEvent.change(screen.getByLabelText(label), { target: { value } });
    fireEvent.mouseDown(within(screen.getByTestId('shipping-field')).getByRole('combobox'));
    fireEvent.click(await screen.findByRole('option', { name: /Standard/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Place order' }));
  }

  it('shows validation errors without calling the API', async () => {
    let posts = 0;
    api.defaults.adapter = async (config) => {
      if (config.method === 'post') posts++;
      return respond(config, String(config.url).includes('shipping') ? [{ id: 's1', code: 'STD', name: 'Standard', fee: 30, currencyCode: 'VND' }] : cart);
    };
    renderWith(<CheckoutPage />, '/checkout', '/checkout', ['Customer']);
    fireEvent.click(await screen.findByRole('button', { name: 'Place order' }));
    expect(await screen.findByText('Enter the recipient name.')).toBeInTheDocument();
    expect(screen.getByText('Choose a shipping method.')).toBeInTheDocument();
    expect(posts).toBe(0);
  });

  it('shows a coupon refusal beside its field, and sends the retry after a lost response with the same key', async () => {
    const sent: string[] = [];
    let outcome: 'coupon' | 'lost' | 'ok' = 'coupon';
    api.defaults.adapter = async (config) => {
      const url = String(config.url);
      if (config.method === 'post') {
        sent.push(String(config.headers['Idempotency-Key']));
        if (outcome === 'coupon') throw failure(config, 400, { code: 'VALIDATION_FAILED', correlationId: 'c1', errors: { couponCode: ['This coupon cannot be applied to this order.'] } });
        if (outcome === 'lost') throw new AxiosError('Network Error', 'ERR_NETWORK', config);
        return respond(config, { id: 'o1' }, 202);
      }
      return respond(config, url.includes('shipping') ? [{ id: 's1', code: 'STD', name: 'Standard', fee: 30, currencyCode: 'VND' }] : cart);
    };
    renderWith(<CheckoutPage />, '/checkout', '/checkout', ['Customer']);
    await fillAndSubmit();
    expect(await screen.findByText('This coupon cannot be applied to this order.')).toBeInTheDocument();
    expect(sent).toHaveLength(1);

    outcome = 'lost';
    fireEvent.click(screen.getByRole('button', { name: 'Place order' }));
    expect(await screen.findByTestId('retry-hint')).toBeInTheDocument();
    expect(sent).toHaveLength(2);
    expect(sent[1]).not.toBe(sent[0]);

    outcome = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Place order' }));
    expect(await screen.findByText('accepted page')).toBeInTheDocument();
    expect(sent).toHaveLength(3);
    expect(sent[2]).toBe(sent[1]);
  });

  it('sends an empty cart back to the cart page', async () => {
    api.defaults.adapter = async (config) => respond(config, String(config.url).includes('shipping') ? [] : { id: null, items: [], subtotal: 0 });
    renderWith(<CheckoutPage />, '/checkout', '/checkout', ['Customer']);
    expect(await screen.findByText('cart page')).toBeInTheDocument();
  });
});

describe('role visibility', () => {
  function renderMenu(roles: string[]) {
    api.defaults.adapter = async (config) => respond(config, { id: null, items: [], subtotal: 0 });
    renderWith(<AppLayout />, '/', '/', roles);
  }

  it('shows a customer no staff menu', async () => {
    renderMenu(['Customer']);
    expect(await screen.findByTestId('cart-link')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Staff' })).not.toBeInTheDocument();
  });

  it('shows an Inventory Manager only the pages the API allows', async () => {
    renderMenu(['InventoryManager']);
    fireEvent.click(await screen.findByRole('button', { name: 'Staff' }));
    const labels = (await screen.findAllByRole('menuitem')).map((item) => item.textContent);
    expect(labels).toEqual(['Inventory', 'Shipments', 'Returns']);
    expect(screen.queryByTestId('cart-link')).not.toBeInTheDocument();
  });

  it('guards every staff route with a role', () => {
    const paths: string[] = [];
    const walk = (nodes: typeof routes, guarded: boolean) => nodes.forEach((node) => {
      const guard = guarded || (node.element as { props?: { roles?: string[] } } | undefined)?.props?.roles !== undefined;
      if (node.path?.startsWith('/admin') && !guard) paths.push(node.path);
      if (node.children) walk(node.children, guard);
    });
    walk(routes, false);
    expect(paths).toEqual([]);
  });
});
