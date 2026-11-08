import { Button, Stack, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { z } from 'zod';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { FormDialog } from '../../components/FormDialog';
import { Pager, StatusFilter, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import { StatusChip } from '../../components/StatusChip';
import type { Paged, ReturnRequest } from '../../types/api';
import { useAuth } from '../auth/AuthProvider';

const statuses = ['Requested', 'Approved', 'Rejected', 'Received', 'Refunded', 'Closed'] as const;
const rejectSchema = z.object({ reason: z.string().trim().min(1, 'A rejection needs a reason.').max(500) });
const receiveSchema = z.record(z.string(), z.boolean());

export function ReturnsAdminPage() {
  const { page, get, update } = useListParams();
  const { hasRole } = useAuth();
  const status = get('status');
  const [rejecting, setRejecting] = useState<ReturnRequest | null>(null);
  const [receiving, setReceiving] = useState<ReturnRequest | null>(null);
  const returns = useQuery({
    queryKey: ['admin-returns', status, page],
    staleTime: 0,
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.get<Paged<ReturnRequest>>('/admin/returns', { params: { status: status || undefined, page } })).data
  });
  const keys = [['admin-returns']];
  const approve = useAction((id: string) => api.post(`/admin/returns/${id}/approve`), keys);
  const close = useAction((id: string) => api.post(`/admin/returns/${id}/close`), keys);
  const reject = useAction(({ id, reason }: { id: string; reason: string }) => api.post(`/admin/returns/${id}/reject`, { reason }), keys);
  const receive = useAction(({ id, items }: { id: string; items: { orderItemId: string; restock: boolean }[] }) => api.post(`/admin/returns/${id}/receive`, { items }), keys);
  const orderStaff = hasRole('Support', 'Admin');
  const fulfillment = hasRole('InventoryManager', 'Admin');
  const failure = approve.error ?? close.error;

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Returns</Typography>
      {failure && <ErrorState error={failure} />}
      <StatusFilter value={status} options={statuses} onChange={(value) => update({ status: value || null })} />
      {returns.isPending ? <LoadingState label="Loading returns" />
        : returns.isError ? <ErrorState error={returns.error} onRetry={() => returns.refetch()} />
        : returns.data.items.length === 0 ? <EmptyState title="No returns found" />
        : (
          <>
            <DataTable
              rowKey={(item) => item.id}
              rows={returns.data.items}
              columns={[
                { header: 'Order', render: (item) => <code>{item.orderId}</code> },
                { header: 'Reason', render: (item) => item.reason },
                { header: 'Items', align: 'right', render: (item) => item.items.reduce((sum, line) => sum + line.quantity, 0) },
                { header: 'Status', render: (item) => <StatusChip status={item.status} /> },
                { header: '', render: (item) => (
                  <Stack direction="row" spacing={1}>
                    {item.status === 'Requested' && orderStaff && <Button size="small" onClick={() => approve.mutate(item.id)}>Approve</Button>}
                    {item.status === 'Requested' && orderStaff && <Button size="small" color="error" onClick={() => setRejecting(item)}>Reject</Button>}
                    {item.status === 'Approved' && fulfillment && <Button size="small" onClick={() => setReceiving(item)}>Receive items</Button>}
                    {item.status === 'Refunded' && orderStaff && <Button size="small" onClick={() => close.mutate(item.id)}>Close</Button>}
                  </Stack>
                ) }
              ]}
            />
            <Pager page={returns.data.page} totalPages={returns.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
      <FormDialog
        open={rejecting !== null} title="Reject this return" submitLabel="Reject" schema={rejectSchema}
        fields={[{ name: 'reason', label: 'Reason', multiline: true }]} defaults={{ reason: '' }}
        onSubmit={({ reason }) => reject.mutateAsync({ id: rejecting!.id, reason })} onClose={() => setRejecting(null)}
      />
      <FormDialog
        open={receiving !== null} title="Receive returned items" submitLabel="Confirm receipt" schema={receiveSchema}
        fields={(receiving?.items ?? []).map((line, index) => ({ name: line.orderItemId, label: `Restock line ${index + 1} (quantity ${line.quantity})`, type: 'checkbox' as const }))}
        defaults={Object.fromEntries((receiving?.items ?? []).map((line) => [line.orderItemId, true]))}
        onSubmit={(values) => receive.mutateAsync({ id: receiving!.id, items: receiving!.items.map((line) => ({ orderItemId: line.orderItemId, restock: !!values[line.orderItemId] })) })}
        onClose={() => setReceiving(null)}
      />
    </Stack>
  );
}
