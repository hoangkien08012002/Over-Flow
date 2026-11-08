import { Link, Stack, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link as RouterLink } from 'react-router-dom';
import { api } from '../../api/client';
import { DataTable } from '../../components/DataTable';
import { formatMoney } from '../../components/format';
import { Pager, StatusFilter, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import { StatusChip } from '../../components/StatusChip';
import type { Order, Paged } from '../../types/api';
import { orderStatuses } from './orderStatus';

export function OrdersPage() {
  const { page, get, update } = useListParams();
  const status = get('status');
  const query = useQuery({
    queryKey: ['orders', status, page],
    staleTime: 0,
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.get<Paged<Order>>('/orders', { params: { status: status || undefined, page } })).data
  });
  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">My orders</Typography>
      <StatusFilter value={status} options={orderStatuses} onChange={(value) => update({ status: value || null })} />
      {query.isPending ? <LoadingState label="Loading your orders" />
        : query.isError ? <ErrorState error={query.error} onRetry={() => query.refetch()} />
        : query.data.items.length === 0 ? <EmptyState title={status ? 'No orders with this status' : 'You have no orders yet'} />
        : (
          <>
            <DataTable
              rowKey={(order) => order.id}
              rows={query.data.items}
              columns={[
                { header: 'Order', render: (order) => <Link component={RouterLink} to={`/orders/${order.id}`}>{order.orderNumber}</Link> },
                { header: 'Status', render: (order) => <StatusChip status={order.status} /> },
                { header: 'Total', align: 'right', render: (order) => formatMoney(order.grandTotal, order.currencyCode) }
              ]}
            />
            <Pager page={query.data.page} totalPages={query.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
    </Stack>
  );
}
