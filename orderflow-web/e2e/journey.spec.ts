import { spawn, type ChildProcess } from 'node:child_process';
import { createWriteStream } from 'node:fs';
import { dirname, join } from 'node:path';
import { expect, test, type Browser, type BrowserContext, type Page } from '@playwright/test';

// The core journeys through a real browser, a real API, a real Worker, SQL Server, and Kafka. Nothing here is mocked except one lost response.
// The tests share one store, so they run in order. The Worker starts partway through, which holds orders in PendingInventory until then.
test.describe.configure({ mode: 'serial' });

const env = (name: string) => {
  const value = process.env[name];
  if (!value) throw new Error(`${name} is not set. Run this through tests/e2e/run-browser-e2e.ps1.`);
  return value;
};

const password = 'StrongPassword123!';
const runId = Date.now().toString(36).toUpperCase();
const categoryName = `Browser ${runId}`;
const productName = `Desk lamp ${runId}`;
const otherProductName = `Bookshelf ${runId}`;
const couponCode = `SAVE${runId}`;
const customerEmail = `browser-${runId}@example.invalid`;

let worker: ChildProcess | undefined;
const contexts: BrowserContext[] = [];
let admin: Page, inventory: Page, support: Page, customer: Page;

async function open(browser: Browser) {
  const context = await browser.newContext();
  contexts.push(context);
  return context.newPage();
}

async function signIn(page: Page, email: string) {
  await page.goto('/login');
  await page.getByLabel('Email').fill(email);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByTestId('signed-in-as')).toHaveText(email);
}

async function staffPage(page: Page, label: string) {
  await page.getByRole('button', { name: 'Staff' }).click();
  await page.getByRole('menuitem', { name: label, exact: true }).click();
}

async function choose(page: Page, label: string, option: string | RegExp) {
  await page.getByRole('combobox', { name: label }).click();
  await page.getByRole('option', { name: option }).click();
}

async function addToCart(page: Page, name: string, quantity = 1) {
  await page.getByRole('link', { name: 'Products', exact: true }).click();
  await page.getByLabel('Search').fill(name);
  await page.getByRole('button', { name: 'Apply' }).click();
  await page.getByRole('link', { name }).click();
  await page.getByLabel('Quantity').fill(String(quantity));
  await page.getByRole('button', { name: 'Add to cart' }).click();
  await expect(page.getByText('Added to your cart.')).toBeVisible();
}

async function fillCheckout(page: Page, payment: string | RegExp, coupon = '') {
  await page.getByTestId('cart-link').click();
  await page.getByRole('link', { name: 'Go to checkout' }).click();
  await page.getByLabel('Recipient name').fill('Browser Buyer');
  await page.getByLabel('Phone').fill('0900000000');
  await page.getByLabel('Street address').fill('1 Test Street');
  await page.getByLabel('Ward').fill('Ward 1');
  await page.getByLabel('District').fill('District 1');
  await page.getByLabel('Province').fill('Thanh Hoa');
  await choose(page, 'Shipping method', /.+/);
  await choose(page, 'Payment method', payment);
  await page.getByLabel('Coupon code (optional)').fill(coupon);
}

const placeOrder = (page: Page) => page.getByRole('button', { name: 'Place order' }).click();
const status = (page: Page) => page.getByTestId('status').first();
const orderIdOf = (page: Page) => new URL(page.url()).pathname.split('/')[2];

// A page holds its status until the next read, so a step taken by staff in another browser is seen on reload.
async function reloadUntilStatus(page: Page, expected: string) {
  await expect(async () => {
    await page.reload();
    await expect(status(page)).toHaveText(expected, { timeout: 2_000 });
  }).toPass({ timeout: 60_000 });
}

test.beforeAll(async ({ browser }) => {
  admin = await open(browser);
  inventory = await open(browser);
  support = await open(browser);
  customer = await open(browser);
  await signIn(admin, env('E2E_ADMIN_EMAIL'));
  await signIn(inventory, env('E2E_INVENTORY_EMAIL'));
  await signIn(support, env('E2E_SUPPORT_EMAIL'));
});

test.afterAll(async () => {
  worker?.kill();
  await Promise.all(contexts.map((context) => context.close()));
});

test('a guest registers, stays signed in across a reload, and cannot reach staff pages', async () => {
  await customer.goto('/orders');
  await expect(customer).toHaveURL(/\/login$/);

  await customer.getByRole('link', { name: 'Create one' }).click();
  await customer.getByLabel('First name').fill('Browser');
  await customer.getByLabel('Last name').fill('Buyer');
  await customer.getByLabel('Email').fill(customerEmail);
  await customer.getByLabel('Password').fill('short');
  await customer.getByRole('button', { name: 'Create account' }).click();
  await expect(customer.getByText('Use at least 12 characters.')).toBeVisible();
  await customer.getByLabel('Password').fill(password);
  await customer.getByRole('button', { name: 'Create account' }).click();
  await expect(customer.getByTestId('signed-in-as')).toHaveText(customerEmail);

  await customer.reload();
  await expect(customer.getByTestId('signed-in-as')).toHaveText(customerEmail);
  const stored = await customer.evaluate(() => JSON.stringify({ ...localStorage, ...sessionStorage }));
  expect(stored).toBe('{}');
  const cookies = await customer.context().cookies();
  expect(cookies.find((cookie) => cookie.name === 'orderflow_refresh')).toMatchObject({ httpOnly: true, sameSite: 'Strict' });

  await expect(customer.getByRole('button', { name: 'Staff' })).toHaveCount(0);
  await customer.goto('/admin/products');
  await expect(customer).toHaveURL(/\/forbidden$/);
  await customer.goto('/no-such-page');
  await expect(customer.getByText(/not found/i)).toBeVisible();
});

test('an Admin builds the catalog and coupon, and API errors show their correlation ID', async () => {
  await staffPage(admin, 'Categories');
  await admin.getByRole('button', { name: 'New category' }).click();
  await admin.getByRole('button', { name: 'Save' }).click();
  await expect(admin.getByText('Enter a name.')).toBeVisible();
  await admin.getByLabel('Name').fill(categoryName);
  await admin.getByLabel('Slug').fill(`browser-${runId}`);
  await admin.getByRole('button', { name: 'Save' }).click();
  await expect(admin.getByRole('cell', { name: categoryName, exact: true })).toBeVisible();

  await admin.getByRole('button', { name: 'New category' }).click();
  await admin.getByLabel('Name').fill(`${categoryName} copy`);
  await admin.getByLabel('Slug').fill(`browser-${runId}`);
  await admin.getByRole('button', { name: 'Save' }).click();
  await expect(admin.getByText('Another category already uses this slug.')).toBeVisible();
  await expect(admin.getByTestId('correlation-id')).toContainText(/\S{8,}/);
  await admin.getByRole('button', { name: 'Close' }).click();

  await staffPage(admin, 'Products');
  for (const [name, sku, price] of [[productName, `LAMP-${runId}`, '100000'], [otherProductName, `SHELF-${runId}`, '250000']]) {
    await admin.getByRole('button', { name: 'New product' }).click();
    await choose(admin, 'Category', categoryName);
    await admin.getByLabel('SKU').fill(sku);
    await admin.getByLabel('Name').fill(name);
    await admin.getByLabel('Price').fill(price);
    await admin.getByRole('button', { name: 'Save' }).click();
    await expect(admin.getByRole('dialog')).toHaveCount(0);
  }
  await admin.getByLabel('Search').fill(runId);
  await admin.getByLabel('Search').press('Enter');
  await expect(admin.getByRole('cell', { name: productName, exact: true })).toBeVisible();

  await admin.getByRole('button', { name: `Edit ${productName}` }).click();
  await admin.getByLabel('Description').fill('A warm light for late work.');
  await admin.getByRole('button', { name: 'Save' }).click();
  await expect(admin.getByRole('dialog')).toHaveCount(0);

  await staffPage(admin, 'Coupons');
  await admin.getByRole('button', { name: 'New coupon' }).click();
  await admin.getByLabel('Code').fill(couponCode);
  await admin.getByRole('button', { name: 'Save' }).click();
  await expect(admin.getByRole('cell', { name: couponCode, exact: true })).toBeVisible();
});

test('an Inventory Manager adds stock, and sees only their own pages', async () => {
  await inventory.getByRole('button', { name: 'Staff' }).click();
  await expect(inventory.getByRole('menuitem')).toHaveText(['Inventory', 'Shipments', 'Returns']);
  await inventory.keyboard.press('Escape');
  await staffPage(inventory, 'Inventory');
  await inventory.getByLabel('Search').fill(runId);
  await inventory.getByLabel('Search').press('Enter');
  for (const name of [productName, otherProductName]) {
    await inventory.getByRole('link', { name }).click();
    await expect(inventory.getByTestId('stock')).toContainText('On hand 0');
    await inventory.getByRole('button', { name: 'Adjust stock' }).click();
    await inventory.getByLabel('Quantity change').fill('20');
    await inventory.getByRole('button', { name: 'Adjust', exact: true }).click();
    await expect(inventory.getByTestId('stock')).toContainText('On hand 20 · Reserved 0 · Available 20');
    await inventory.goBack();
  }
});

let codOrderId = '';

test('a customer filters, fills a cart, and checks out, with a lost response retried under the same key', async () => {
  await customer.getByRole('link', { name: 'Products', exact: true }).click();
  await customer.getByLabel('Search').fill(runId);
  await customer.getByRole('button', { name: 'Apply' }).click();
  await expect(customer.getByRole('link', { name: productName })).toBeVisible();
  await choose(customer, 'Sort', 'Price, high to low');
  await expect(customer.getByRole('row').nth(1)).toContainText(otherProductName);
  await customer.getByLabel('Max price').fill('150000');
  await customer.getByRole('button', { name: 'Apply' }).click();
  await expect(customer.getByRole('link', { name: otherProductName })).toHaveCount(0);
  await expect(customer).toHaveURL(/maxPrice=150000/);

  await customer.getByRole('link', { name: productName }).click();
  await expect(customer.getByTestId('product-available')).toHaveText('20 available');
  await customer.getByRole('button', { name: 'Add to wishlist' }).click();
  await expect(customer.getByRole('button', { name: 'Remove from wishlist' })).toBeVisible();
  await customer.getByLabel('Quantity').fill('1');
  await customer.getByRole('button', { name: 'Add to cart' }).click();
  await expect(customer.getByTestId('cart-link')).toHaveText('Cart (1)');

  await customer.getByTestId('cart-link').click();
  await customer.getByLabel(`Quantity of ${productName}`).fill('2');
  await customer.getByLabel(`Quantity of ${productName}`).blur();
  await expect(customer.getByTestId('cart-subtotal')).toHaveText('Subtotal: 200,000 VND');

  await customer.getByRole('link', { name: 'Go to checkout' }).click();
  await placeOrder(customer);
  await expect(customer.getByText('Enter the recipient name.')).toBeVisible();

  await fillCheckout(customer, 'Cash on delivery', 'NOT-A-COUPON');
  await placeOrder(customer);
  await expect(customer.getByText('This coupon cannot be applied to this order.')).toBeVisible();

  // The first request reaches the server and commits, but the browser never sees the answer.
  const keys: string[] = [];
  let dropped = false;
  await customer.route('**/api/v1/orders', async (route) => {
    keys.push(route.request().headers()['idempotency-key']);
    if (dropped) return route.continue();
    dropped = true;
    await route.fetch();
    return route.abort('connectionreset');
  });
  await customer.getByLabel('Coupon code (optional)').fill(couponCode);
  await placeOrder(customer);
  await expect(customer.getByTestId('retry-hint')).toBeVisible();
  await placeOrder(customer);
  await expect(customer.getByTestId('accepted-banner')).toBeVisible();
  await customer.unroute('**/api/v1/orders');
  expect(keys).toHaveLength(2);
  expect(keys[1]).toBe(keys[0]);
  codOrderId = orderIdOf(customer);

  // With the Worker not yet running, the order stays where the API left it.
  await expect(status(customer)).toHaveText('PendingInventory');
  await expect(customer.getByTestId('discount')).toHaveText('Discount: 20,000 VND');
  await expect(customer.getByTestId('grand-total')).toHaveText(/Total: 2\d\d,000 VND/);

  await customer.getByRole('link', { name: 'My orders' }).click();
  await expect(customer.getByRole('row')).toHaveCount(2);
  await expect(customer.getByTestId('cart-link')).toHaveText('Cart (0)');
});

test('a customer cancels a pending order, and a waiting order advances by itself when the Worker starts', async () => {
  await addToCart(customer, otherProductName);
  await fillCheckout(customer, 'Card (simulation)');
  await placeOrder(customer);
  await expect(customer.getByTestId('accepted-banner')).toBeVisible();
  await customer.getByRole('button', { name: 'Cancel order' }).click();
  await customer.getByLabel('Reason (optional)').fill('Changed my mind');
  await customer.getByRole('dialog').getByRole('button', { name: 'Cancel order' }).click();
  await expect(status(customer)).toHaveText('Cancelled');
  await expect(customer.getByTestId('history')).toContainText('Changed my mind');
  await expect(customer.getByRole('button', { name: 'Cancel order' })).toHaveCount(0);

  await addToCart(customer, otherProductName);
  await fillCheckout(customer, 'Bank transfer (simulation)');
  await placeOrder(customer);
  await expect(status(customer)).toHaveText('PendingInventory');

  const dll = env('E2E_WORKER_DLL');
  const log = createWriteStream(join(env('E2E_LOG_DIR'), 'browser-worker.log'));
  worker = spawn('dotnet', [dll], { cwd: dirname(dll), stdio: ['ignore', 'pipe', 'pipe'] });
  worker.stdout?.pipe(log);
  worker.stderr?.pipe(log);

  // No reload or click from here: only polling can change what this page shows.
  await expect(status(customer)).toHaveText('Processing', { timeout: 120_000 });
  await expect(customer.getByTestId('status-message')).toHaveText('Your order is confirmed and being prepared.');
  await expect(customer.getByTestId('history')).toContainText('AwaitingPayment');

  await customer.goto(`/orders/${codOrderId}`);
  await expect(status(customer)).toHaveText('Processing', { timeout: 60_000 });
});

test('staff ship an order and the customer reviews and returns it, through to the refund', async () => {
  // The Inventory Manager cannot list orders, so a shipment starts from the committed reservation.
  await staffPage(inventory, 'Inventory');
  await inventory.getByLabel('Search').fill(productName);
  await inventory.getByLabel('Search').press('Enter');
  await inventory.getByRole('link', { name: productName }).click();
  await expect(inventory.getByTestId('stock')).toContainText('On hand 18');
  await inventory.getByRole('link', { name: 'Ship' }).click();
  await expect(inventory.getByLabel('Order ID')).toHaveValue(codOrderId);
  await inventory.getByRole('button', { name: 'Create shipment' }).click();
  const row = inventory.getByRole('row', { name: new RegExp(codOrderId) });
  await expect(row).toBeVisible();
  await row.getByRole('button', { name: 'Mark ready' }).click();
  await row.getByRole('button', { name: 'Dispatch' }).click();
  await reloadUntilStatus(customer, 'Shipped');
  await expect(customer.getByTestId('shipment')).toContainText('OFT-');
  await row.getByRole('button', { name: 'Mark delivered' }).click();
  await reloadUntilStatus(customer, 'Delivered');

  await customer.getByRole('button', { name: 'Review' }).click();
  await customer.getByLabel('Rating (1 to 5)').fill('9');
  await customer.getByRole('button', { name: 'Submit review' }).click();
  await expect(customer.getByText('Use 1 to 5.')).toBeVisible();
  await customer.getByLabel('Rating (1 to 5)').fill('4');
  await customer.getByLabel('Comment (optional)').fill('Bright and steady.');
  await customer.getByRole('button', { name: 'Submit review' }).click();
  await expect(customer.getByText('Thanks for your review.')).toBeVisible();

  await customer.getByRole('button', { name: 'Request a return' }).click();
  await customer.getByLabel('Reason').fill('Too bright');
  await customer.getByRole('button', { name: 'Request return' }).click();
  await expect(customer.getByTestId('return')).toContainText('Requested');
  await expect(customer.getByRole('button', { name: 'Request a return' })).toHaveCount(0);

  await staffPage(support, 'Returns');
  const supportRow = support.getByRole('row', { name: new RegExp(codOrderId) });
  await expect(supportRow.getByRole('button', { name: 'Receive items' })).toHaveCount(0);
  await supportRow.getByRole('button', { name: 'Approve' }).click();
  await expect(supportRow.getByTestId('status')).toHaveText('Approved');

  await staffPage(inventory, 'Returns');
  await inventory.getByRole('row', { name: new RegExp(codOrderId) }).getByRole('button', { name: 'Receive items' }).click();
  await inventory.getByRole('button', { name: 'Confirm receipt' }).click();
  await expect(inventory.getByRole('dialog')).toHaveCount(0);

  await reloadUntilStatus(customer, 'Refunded');
  await expect(customer.getByTestId('status-message')).toHaveText('This order was returned and refunded.');

  await staffPage(admin, 'Returns');
  const adminRow = admin.getByRole('row', { name: new RegExp(codOrderId) });
  await expect(async () => {
    await admin.reload();
    await expect(adminRow.getByTestId('status')).toHaveText('Refunded', { timeout: 2_000 });
  }).toPass({ timeout: 60_000 });
  await adminRow.getByRole('button', { name: 'Close' }).click();
  await expect(adminRow.getByTestId('status')).toHaveText('Closed');
});

test('Support cancels a paid order with a reason, and the Admin sees it in the audit log', async () => {
  await addToCart(customer, productName);
  await fillCheckout(customer, 'Card (simulation)');
  await placeOrder(customer);
  await expect(status(customer)).toHaveText('Processing', { timeout: 60_000 });
  const orderId = orderIdOf(customer);

  await support.goto(`/admin/orders/${orderId}`);
  await expect(status(support)).toHaveText('Processing');
  await expect(support.getByRole('button', { name: 'Create shipment' })).toHaveCount(0);
  await support.getByRole('button', { name: 'Cancel order' }).click();
  await support.getByRole('dialog').getByRole('button', { name: 'Cancel order' }).click();
  await expect(support.getByText('A staff cancellation needs a reason.')).toBeVisible();
  await support.getByLabel('Reason').fill('Damaged in the warehouse');
  await support.getByRole('dialog').getByRole('button', { name: 'Cancel order' }).click();
  await expect(status(support)).toHaveText('Cancelled');
  await reloadUntilStatus(customer, 'Cancelled');

  await staffPage(admin, 'Audit log');
  await expect(admin.getByRole('row', { name: /order\.staff-cancel/ }).first()).toContainText('Damaged in the warehouse');
});

test('an Admin moderates a review, adds a shipping method, and opens the operations queues', async () => {
  await customer.goto('/');
  await customer.getByLabel('Search').fill(productName);
  await customer.getByRole('button', { name: 'Apply' }).click();
  await customer.getByRole('link', { name: productName }).click();
  await expect(customer.getByTestId('review-summary')).toContainText('4 out of 5 from 1');
  await expect(customer.getByText('Bright and steady.')).toBeVisible();

  await staffPage(admin, 'Reviews');
  const review = admin.getByRole('row', { name: /Bright and steady/ });
  await review.getByRole('button', { name: 'Hide' }).click();
  await admin.getByLabel('Reason').fill('Off topic');
  await admin.getByRole('dialog').getByRole('button', { name: 'Hide' }).click();
  await expect(review.getByTestId('status')).toHaveText('Hidden');
  await customer.reload();
  await expect(customer.getByText('No reviews yet.')).toBeVisible();

  await staffPage(admin, 'Coupons');
  await admin.getByRole('button', { name: `Usage of ${couponCode}` }).click();
  await expect(admin.getByTestId('coupon-usage')).toContainText(`${couponCode}: `);

  await staffPage(admin, 'Shipping methods');
  await admin.getByRole('button', { name: 'New shipping method' }).click();
  await admin.getByLabel('Code').fill(`X${runId}`);
  await admin.getByLabel('Name').fill('Express by browser');
  await admin.getByLabel('Fee').fill('50000');
  await admin.getByRole('button', { name: 'Save' }).click();
  await expect(admin.getByRole('cell', { name: 'Express by browser', exact: true })).toBeVisible();

  await staffPage(admin, 'Operations');
  await expect(admin.getByRole('heading', { name: 'Failed messages' })).toBeVisible();
  await expect(admin.getByText('No failed refunds')).toBeVisible();
});

test('signing out ends the session and removes the private pages', async () => {
  await customer.getByRole('button', { name: 'Sign out' }).click();
  await expect(customer.getByRole('link', { name: 'Sign in' })).toBeVisible();
  await expect(customer.getByTestId('cart-link')).toHaveCount(0);
  await customer.goto(`/orders/${codOrderId}`);
  await expect(customer).toHaveURL(/\/login$/);
});
