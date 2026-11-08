import { Chip } from '@mui/material';

const colors: Record<string, 'success' | 'error' | 'warning' | 'info'> = {
  Processing: 'success', Delivered: 'success', Refunded: 'success', Succeeded: 'success', Committed: 'success', Replayed: 'success', Published: 'success', Closed: 'success',
  Approved: 'info', Received: 'info', Shipped: 'info', ReadyToShip: 'info', Paid: 'info',
  PendingInventory: 'warning', AwaitingPayment: 'warning', Pending: 'warning', Active: 'warning', Requested: 'warning', ReplayRequested: 'warning',
  Rejected: 'error', PaymentFailed: 'error', Cancelled: 'error', Failed: 'error', DeliveryFailed: 'error', Hidden: 'error', Expired: 'error', Released: 'error'
};

export function StatusChip({ status }: { status: string }) {
  return <Chip size="small" label={status} color={colors[status] ?? 'default'} data-testid="status" />;
}
