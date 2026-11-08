import { Alert, Button, Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useParams } from 'react-router-dom';
import { z } from 'zod';
import { api } from '../../api/client';
import { ApiError } from '../../api/errors';
import { useAction } from '../../api/hooks';
import { FormDialog } from '../../components/FormDialog';
import { ErrorState, LoadingState } from '../../components/PageStates';
import { StatusChip } from '../../components/StatusChip';
import type { OrderDetail, OrderItem, ReturnRequest, Shipment } from '../../types/api';
import { customerCanCancel, orderPollInterval, statusMessages } from './orderStatus';
import { History, OrderSummary, ShipmentPanel } from './OrderSummary';

const reasonSchema = z.object({ reason: z.string().trim().max(500, 'Use at most 500 characters.').optional() });
const returnSchema = z.object({ reason: z.string().trim().min(1, 'Tell us why you are returning this order.').max(500, 'Use at most 500 characters.') });
const reviewSchema = z.object({
  rating: z.coerce.number({ message: 'Enter a rating.' }).int('Use a whole number.').min(1, 'Use 1 to 5.').max(5, 'Use 1 to 5.'),
  comment: z.string().trim().max(2000, 'Use at most 2000 characters.').optional()
});

const shippedStatuses = new Set(['Processing', 'Shipped', 'Delivered', 'Refunded']);
const returnStatuses = new Set(['Delivered', 'Refunded']);

export function OrderDetailPage({ accepted = false }: { accepted?: boolean }) {
  const { id = '' } = useParams();
  const [dialog, setDialog] = useState<'cancel' | 'return' | null>(null);
  const [reviewing, setReviewing] = useState<OrderItem | null>(null);
  const detail = useQuery({
    queryKey: ['order', id],
    staleTime: 0,
    queryFn: async () => (await api.get<OrderDetail>(`/orders/${id}`)).data,
    refetchInterval: (query) => orderPollInterval(query.state.data?.order.status)
  });
  const status = detail.data?.order.status;
  const shipment = useQuery({
    queryKey: ['order', id, 'shipment', status],
    enabled: !!status && shippedStatuses.has(status),
    staleTime: 0,
    queryFn: async () => {
      // An order without a shipment is a normal state here, not an error to show.
      try { return (await api.get<Shipment>(`/orders/${id}/shipment`)).data; }
      catch (error) { if (error instanceof ApiError && error.status === 404) return null; throw error; }
    }
  });
  const returns = useQuery({
    queryKey: ['order', id, 'returns'],
    enabled: !!status && returnStatuses.has(status),
    staleTime: 0,
    queryFn: async () => (await api.get<ReturnRequest[]>(`/orders/${id}/returns`)).data
  });
  const cancel = useAction((reason: string | undefined) => api.post(`/orders/${id}/cancel`, { reason: reason || null }), [['order', id], ['orders']]);
  const requestReturn = useAction((reason: string) => api.post(`/orders/${id}/returns`, { reason }), [['order', id, 'returns']]);
  const review = useAction(({ item, rating, comment }: { item: OrderItem; rating: number; comment?: string }) =>
    api.post(`/products/${item.productId}/reviews`, { orderItemId: item.id, rating, comment: comment || null }), [['reviews']]);

  if (detail.isPending) return <LoadingState label="Loading the order" />;
  if (detail.isError) return <ErrorState error={detail.error} onRetry={() => detail.refetch()} />;
  const { order, history } = detail.data;
  const openReturn = returns.data?.find((item) => item.status !== 'Rejected');

  return (
    <Stack spacing={2} sx={{ maxWidth: 860 }}>
      {accepted && (
        <Alert severity="success" data-testid="accepted-banner">
          Order received. This confirms we have stored your order, not that it has succeeded. This page follows its progress.
        </Alert>
      )}
      <Stack direction="row" useFlexGap spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        <Typography variant="h5" component="h1">Order {order.orderNumber}</Typography>
        <StatusChip status={order.status} />
      </Stack>
      <Typography data-testid="status-message">{statusMessages[order.status] ?? order.status}</Typography>
      {cancel.error && <ErrorState error={cancel.error} />}
      {review.isSuccess && <Alert severity="success">Thanks for your review.</Alert>}
      <Stack direction="row" spacing={1}>
        {customerCanCancel(order.status) && <Button color="error" variant="outlined" onClick={() => setDialog('cancel')}>Cancel order</Button>}
        {order.status === 'Delivered' && returns.isSuccess && !openReturn && <Button variant="outlined" onClick={() => setDialog('return')}>Request a return</Button>}
      </Stack>
      <OrderSummary
        detail={detail.data}
        itemAction={order.status === 'Delivered' ? (item) => <Button size="small" onClick={() => setReviewing(item)}>Review</Button> : undefined}
      />
      {shipment.data && <ShipmentPanel shipment={shipment.data} />}
      {shipment.isError && <ErrorState error={shipment.error} onRetry={() => shipment.refetch()} />}
      {returns.isError && <ErrorState error={returns.error} onRetry={() => returns.refetch()} />}
      {returns.data?.map((item) => (
        <Stack key={item.id} spacing={1} data-testid="return">
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
            <Typography variant="h6" component="h2">Return</Typography>
            <StatusChip status={item.status} />
          </Stack>
          <Typography>Reason: {item.reason}</Typography>
          <History title="Return history" entries={item.history} />
        </Stack>
      ))}
      <History entries={history} />

      <FormDialog
        open={dialog === 'cancel'} title="Cancel this order?" submitLabel="Cancel order" schema={reasonSchema}
        fields={[{ name: 'reason', label: 'Reason (optional)', multiline: true }]} defaults={{ reason: '' }}
        onSubmit={({ reason }) => cancel.mutateAsync(reason)} onClose={() => setDialog(null)}
      />
      <FormDialog
        open={dialog === 'return'} title="Request a return" submitLabel="Request return" schema={returnSchema}
        fields={[{ name: 'reason', label: 'Reason', multiline: true, helperText: 'A return covers the whole order and refunds its full total.' }]} defaults={{ reason: '' }}
        onSubmit={({ reason }) => requestReturn.mutateAsync(reason)} onClose={() => setDialog(null)}
      />
      <FormDialog
        open={reviewing !== null} title={`Review ${reviewing?.productNameSnapshot ?? ''}`} submitLabel="Submit review" schema={reviewSchema}
        fields={[{ name: 'rating', label: 'Rating (1 to 5)', type: 'number' }, { name: 'comment', label: 'Comment (optional)', multiline: true }]} defaults={{ rating: 5, comment: '' }}
        onSubmit={({ rating, comment }) => review.mutateAsync({ item: reviewing!, rating, comment })} onClose={() => setReviewing(null)}
      />
    </Stack>
  );
}
