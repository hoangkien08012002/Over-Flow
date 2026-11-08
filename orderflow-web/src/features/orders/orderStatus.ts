export const orderStatuses = ['PendingInventory', 'AwaitingPayment', 'Paid', 'Processing', 'Shipped', 'Delivered', 'Refunded', 'Rejected', 'PaymentFailed', 'Cancelled'] as const;

// While an order is in these states the Worker is about to move it, and nothing else tells the page when it does.
const inProgress = new Set(['PendingInventory', 'AwaitingPayment', 'Paid']);
export const POLL_MS = 2500;
export const orderPollInterval = (status: string | undefined) => (status !== undefined && inProgress.has(status) ? POLL_MS : false);

export const customerCanCancel = (status: string) => status === 'PendingInventory' || status === 'AwaitingPayment';
export const staffCanCancel = (status: string) => status === 'Paid' || status === 'Processing';

export const statusMessages: Record<string, string> = {
  PendingInventory: 'We received your order and are checking stock.',
  AwaitingPayment: 'Your items are held and payment is running.',
  Paid: 'Payment accepted. We are confirming your items.',
  Processing: 'Your order is confirmed and being prepared.',
  Shipped: 'The carrier has your parcel.',
  Delivered: 'Your order was delivered.',
  Refunded: 'This order was returned and refunded.',
  Rejected: 'An item was out of stock, so the order was rejected. You were not charged.',
  PaymentFailed: 'The payment was declined and your items were released.',
  Cancelled: 'This order was cancelled. Any charge already taken is refunded.'
};
