// Benchmarks the SRS targets: cached product detail, uncached product search, and order submission.
// Run through tests/perf/run-benchmark.ps1, which prepares the data and records the machine.
import { check } from 'k6';
import http from 'k6/http';
import { Trend } from 'k6/metrics';

const base = __ENV.API_URL;
const customers = Number(__ENV.CUSTOMERS || 10);
// k6 reuses VU numbers across scenarios, so each number gets its own customer. Two VUs sharing a cart would measure a conflict the workload does not have.
const accounts = Math.max(customers, 20);
const duration = __ENV.DURATION || '60s';
const warmUp = __ENV.WARMUP || '15s';
const json = { 'Content-Type': 'application/json' };

// Custom trends record only the request under test, so the cart call before each checkout does not count toward it.
const detail = new Trend('product_detail_ms', true);
const search = new Trend('product_search_ms', true);
const submit = new Trend('checkout_submit_ms', true);

const seconds = (text) => Number(text.replace('s', ''));
const phase = (exec, startSeconds, vus) => ({ executor: 'constant-vus', exec, vus, duration: `${seconds(warmUp) + seconds(duration)}s`, startTime: `${startSeconds}s`, gracefulStop: '10s' });
const length = seconds(warmUp) + seconds(duration) + 15;

export const options = {
  scenarios: {
    detail: phase('readDetail', 0, 20),
    search: phase('readSearch', length, 20),
    checkout: phase('placeOrder', length * 2, customers)
  },
  thresholds: {
    product_detail_ms: ['p(95)<=150'],
    product_search_ms: ['p(95)<=500'],
    checkout_submit_ms: ['p(95)<=1000'],
    checks: ['rate>0.99']
  },
  setupTimeout: '180s',
  summaryTrendStats: ['avg', 'med', 'p(90)', 'p(95)', 'p(99)', 'max']
};

export function setup() {
  const products = http.get(`${base}/api/v1/products?pageSize=100&isAvailable=true`).json('items').map((item) => item.id);
  const shippingMethodId = http.get(`${base}/api/v1/shipping-methods`).json('0.id');
  const tokens = [];
  for (let i = 0; i < accounts; i++) {
    const email = `bench-${Date.now()}-${i}@example.invalid`;
    const password = 'StrongPassword123!';
    const registered = http.post(`${base}/api/v1/auth/register`, JSON.stringify({ email, password, firstName: 'Bench', lastName: 'User' }), { headers: json });
    if (registered.status !== 201) throw new Error(`Registration failed with ${registered.status}`);
    tokens.push(http.post(`${base}/api/v1/auth/login`, JSON.stringify({ email, password }), { headers: json }).json('accessToken'));
  }
  return { products, shippingMethodId, tokens, startedAt: Date.now() };
}

// A scenario's warm-up runs the same load without recording it, which fills the cache and the connection pools.
const measuring = (scenarioStartSeconds, data) => Date.now() - data.startedAt > (scenarioStartSeconds + seconds(warmUp)) * 1000;
const pick = (list) => list[Math.floor(Math.random() * list.length)];

export function readDetail(data) {
  const response = http.get(`${base}/api/v1/products/${pick(data.products)}`);
  check(response, { 'detail is 200': (r) => r.status === 200 });
  if (measuring(0, data)) detail.add(response.timings.duration);
}

export function readSearch(data) {
  const sort = pick(['name', 'price', 'price_desc', 'newest']);
  const response = http.get(`${base}/api/v1/products?search=${encodeURIComponent(pick(['Bench', 'Product 1', 'Product 42']))}&sort=${sort}&page=${1 + Math.floor(Math.random() * 5)}&pageSize=20&isAvailable=true`);
  check(response, { 'search is 200': (r) => r.status === 200 });
  if (measuring(length, data)) search.add(response.timings.duration);
}

export function placeOrder(data) {
  const headers = { ...json, Authorization: `Bearer ${data.tokens[__VU - 1]}` };
  const cart = http.post(`${base}/api/v1/cart/items`, JSON.stringify({ productId: pick(data.products), quantity: 1 }), { headers });
  if (!check(cart, { 'cart is 200': (r) => r.status === 200 })) return;
  const body = {
    cartId: cart.json('id'), shippingMethodId: data.shippingMethodId, paymentMethod: 'COD', couponCode: null,
    deliveryAddress: { recipientName: 'Bench', phone: '0900000000', line1: 'Bench street', ward: 'W', district: 'D', province: 'P', countryCode: 'VN' }
  };
  const response = http.post(`${base}/api/v1/orders`, JSON.stringify(body), { headers: { ...headers, 'Idempotency-Key': `bench-${__VU}-${__ITER}-${Date.now()}` } });
  check(response, { 'checkout is 202': (r) => r.status === 202 });
  if (measuring(length * 2, data)) submit.add(response.timings.duration);
}
