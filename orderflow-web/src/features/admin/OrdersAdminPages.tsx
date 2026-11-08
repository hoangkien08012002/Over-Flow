import { Button, Link, Stack, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { Link as RouterLink, useParams } from 'react-router-dom';
import { z } from 'zod';
import { api } from '../../api/client';
import { ApiError } from '../../api/errors';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { FormDialog } from '../../components/FormDialog';
import { formatMoney } from '../../components/format';
import { Pager, StatusFilter, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import { StatusChip } from '../../components/StatusChip';
import type { Order, OrderDetail, Paged, Shipment } from '../../types/api';
import { useAuth } from '../auth/AuthProvider';
import { orderPollInterval, orderStatuses, staffCanCancel, statusMessages } from '../orders/orderStatus';
import { History, OrderSummary, ShipmentPanel } from '../orders/OrderSummary';

export function OrdersAdminPage() {
  const { page, get, update } = useListParams();
  const status = get('status');
  const orders = useQuery({
    queryKey: ['admin-orders', status, page],
    staleTime: 0,
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.get<Paged<Order>>('/admin/orders', { params: { status: status || undefined, page } })).data
  });
  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Orders</Typography>
      <StatusFilter value={status} options={orderStatuses} onChange={(value) => update({ status: value || null })} />
      {orders.isPending ? <LoadingState label="Loading orders" />
        : orders.isError ? <ErrorState error={orders.error} onRetry={() => orders.refetch()} />
        : orders.data.items.length === 0 ? <EmptyState title="No orders found" />
        : (
          <>
            <DataTable
              rowKey={(order) => order.id}
              rows={orders.data.items}
              columns={[
                { header: 'Order', render: (order) => <Link component={RouterLink} to={`/admin/orders/${order.id}`}>{order.orderNumber}</Link> },
                { header: 'Status', render: (order) => <StatusChip status={order.status} /> },
                { header: 'Total', align: 'right', render: (order) => formatMoney(order.grandTotal, order.currencyCode) }
              ]}
            />
            <Pager page={orders.data.page} totalPages={orders.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
    </Stack>
  );
}

const cancelSchema = z.object({ reason: z.string().trim().min(1, 'A staff cancellation needs a reason.').max(500, 'Use at most 500 characters.') });

export function OrderAdminDetailPage() {
  const { id = '' } = useParams();
  const { hasRole } = useAuth();
  const [cancelling, setCancelling] = useState(false);
  const detail = useQuery({
    queryKey: ['admin-order', id],
    staleTime: 0,
    queryFn: async () => (await api.get<OrderDetail>(`/admin/orders/${id}`)).data,
    refetchInterval: (query) => orderPollInterval(query.state.data?.order.status)
  });
  const status = detail.data?.order.status;
  const shipment = useQuery({
    queryKey: ['admin-order', id, 'shipment', status],
    staleTime: 0,
    queryFn: async () => {
      try { return (await api.get<Shipment>(`/orders/${id}/shipment`)).data; }
      catch (error) { if (error instanceof ApiError && error.status === 404) return null; throw error; }
    }
  });
  const cancel = useAction((reason: string) => api.post(`/admin/orders/${id}/cancel`, { reason }), [['admin-order', id], ['admin-orders']]);

  if (detail.isPending) return <LoadingState label="Loading the order" />;
  if (detail.isError) return <ErrorState error={detail.error} onRetry={() => detail.refetch()} />;
  const { order, history } = detail.data;
  // Dispatch ends staff cancellation even while the order is still catching up to Shipped.
  const dispatched = shipment.data && ['Shipped', 'Delivered', 'DeliveryFailed'].includes(shipment.data.status);
  return (
    <Stack spacing={2} sx={{ maxWidth: 860 }}>
      <Stack direction="row" useFlexGap spacing={1} sx={{ alignItems: 'center', flexWrap: 'wrap' }}>
        <Typography variant="h5" component="h1">Order {order.orderNumber}</Typography>
        <StatusChip status={order.status} />
      </Stack>
      <Typography>{statusMessages[order.status] ?? order.status}</Typography>
      <Stack direction="row" spacing={1}>
        {staffCanCancel(order.status) && !dispatched && <Button color="error" variant="outlined" onClick={() => setCancelling(true)}>Cancel order</Button>}
        {order.status === 'Processing' && !shipment.data && hasRole('Admin', 'InventoryManager') && (
          <Button variant="outlined" component={RouterLink} to={`/admin/shipments?orderId=${order.id}`}>Create shipment</Button>
        )}
      </Stack>
      <OrderSummary detail={detail.data} />
      {shipment.data && <ShipmentPanel shipment={shipment.data} />}
      <History entries={history} />
      <FormDialog
        open={cancelling} title="Cancel this order?" submitLabel="Cancel order" schema={cancelSchema}
        fields={[{ name: 'reason', label: 'Reason', multiline: true, helperText: 'Stock returns now, and any charge is refunded automatically.' }]} defaults={{ reason: '' }}
        onSubmit={({ reason }) => cancel.mutateAsync(reason)} onClose={() => setCancelling(false)}
      />
    </Stack>
  );
}
