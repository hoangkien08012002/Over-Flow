import { Button, Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { formatDate, formatMoney } from '../../components/format';
import { StatusFilter, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import { StatusChip } from '../../components/StatusChip';
import type { FailedMessage, FailedRefund, Paged } from '../../types/api';

// Only safe diagnostics reach this page: the API leaves the raw message out of its response.
export function OperationsPage() {
  const { get, update } = useListParams();
  const status = get('status');
  const messages = useQuery({
    queryKey: ['failed-messages', status],
    staleTime: 0,
    queryFn: async () => (await api.get<Paged<FailedMessage>>('/admin/failed-messages', { params: { status: status || undefined } })).data
  });
  const refunds = useQuery({ queryKey: ['failed-refunds'], staleTime: 0, queryFn: async () => (await api.get<Paged<FailedRefund>>('/admin/failed-refunds')).data });
  const reprocess = useAction((id: string) => api.post(`/admin/failed-messages/${id}/reprocess`), [['failed-messages']]);
  const retry = useAction((id: string) => api.post(`/admin/failed-refunds/${id}/retry`), [['failed-refunds']]);

  return (
    <Stack spacing={3}>
      <Typography variant="h5" component="h1">Operations</Typography>
      <Stack spacing={2}>
        <Typography variant="h6" component="h2">Failed messages</Typography>
        {reprocess.error && <ErrorState error={reprocess.error} />}
        <StatusFilter value={status} options={['Failed', 'ReplayRequested', 'Replayed']} onChange={(value) => update({ status: value || null })} />
        {messages.isPending ? <LoadingState label="Loading failed messages" />
          : messages.isError ? <ErrorState error={messages.error} onRetry={() => messages.refetch()} />
          : messages.data.items.length === 0 ? <EmptyState title="No failed messages" />
          : (
            <DataTable
              rowKey={(message) => message.id}
              rows={messages.data.items}
              columns={[
                { header: 'Event', render: (message) => message.eventType ?? 'Unreadable' },
                { header: 'Consumer', render: (message) => message.consumerName },
                { header: 'Attempts', align: 'right', render: (message) => message.attemptCount },
                { header: 'Error', render: (message) => message.errorSummary },
                { header: 'Failed at', render: (message) => formatDate(message.failedAtUtc) },
                { header: 'Status', render: (message) => <StatusChip status={message.status} /> },
                { header: '', render: (message) => message.status === 'Failed' && message.eventId && <Button size="small" onClick={() => reprocess.mutate(message.id)}>Reprocess</Button> }
              ]}
            />
          )}
      </Stack>
      <Stack spacing={2}>
        <Typography variant="h6" component="h2">Failed refunds</Typography>
        {retry.error && <ErrorState error={retry.error} />}
        {refunds.isPending ? <LoadingState label="Loading failed refunds" />
          : refunds.isError ? <ErrorState error={refunds.error} onRetry={() => refunds.refetch()} />
          : refunds.data.items.length === 0 ? <EmptyState title="No failed refunds" />
          : (
            <DataTable
              rowKey={(refund) => refund.id}
              rows={refunds.data.items}
              columns={[
                { header: 'Order', render: (refund) => <code>{refund.orderId}</code> },
                { header: 'Amount', align: 'right', render: (refund) => formatMoney(refund.amount, refund.currencyCode) },
                { header: 'Attempts', align: 'right', render: (refund) => refund.attemptCount },
                { header: 'Updated', render: (refund) => formatDate(refund.updatedAtUtc) },
                { header: '', render: (refund) => <Button size="small" onClick={() => retry.mutate(refund.id)}>Retry</Button> }
              ]}
            />
          )}
      </Stack>
    </Stack>
  );
}
