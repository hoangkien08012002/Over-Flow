import { Button, Stack, TextField, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { z } from 'zod';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { FormDialog } from '../../components/FormDialog';
import { Pager, StatusFilter, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import { StatusChip } from '../../components/StatusChip';
import type { Paged, Shipment } from '../../types/api';

const statuses = ['Pending', 'ReadyToShip', 'Shipped', 'Delivered', 'DeliveryFailed', 'Cancelled'] as const;
const nextSteps: Record<string, { status: string; label: string }[]> = {
  Pending: [{ status: 'ReadyToShip', label: 'Mark ready' }],
  ReadyToShip: [{ status: 'Shipped', label: 'Dispatch' }],
  Shipped: [{ status: 'Delivered', label: 'Mark delivered' }, { status: 'DeliveryFailed', label: 'Delivery failed' }]
};
const failureSchema = z.object({ reason: z.string().trim().min(1, 'Say why the delivery failed.').max(500) });
const orderIdPattern = /^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i;

export function ShipmentsPage() {
  const { page, get, update } = useListParams();
  const status = get('status');
  // Staff arrive here from an order or reservation with its ID already filled in.
  const [orderId, setOrderId] = useState(get('orderId'));
  const [failing, setFailing] = useState<Shipment | null>(null);
  const shipments = useQuery({
    queryKey: ['shipments', status, page],
    staleTime: 0,
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.get<Paged<Shipment>>('/shipments', { params: { status: status || undefined, page } })).data
  });
  const create = useAction((id: string) => api.post('/shipments', { orderId: id }), [['shipments']]);
  const move = useAction(({ id, next, reason }: { id: string; next: string; reason?: string }) => api.post(`/shipments/${id}/status`, { status: next, reason: reason ?? null }), [['shipments']]);
  const orderIdValid = orderIdPattern.test(orderId.trim());
  const submit = (event: FormEvent) => {
    event.preventDefault();
    if (orderIdValid) create.mutate(orderId.trim(), { onSuccess: () => { setOrderId(''); update({ orderId: null }); } });
  };

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Shipments</Typography>
      <Stack component="form" onSubmit={submit} direction="row" useFlexGap spacing={1} sx={{ flexWrap: 'wrap', alignItems: 'flex-start' }}>
        <TextField size="small" label="Order ID" value={orderId} onChange={(event) => setOrderId(event.target.value)} sx={{ minWidth: 340 }} helperText="The order must be in Processing." />
        <Button type="submit" variant="contained" disabled={!orderIdValid || create.isPending}>Create shipment</Button>
      </Stack>
      {(create.error || move.error) && <ErrorState error={create.error ?? move.error} />}
      <StatusFilter value={status} options={statuses} onChange={(value) => update({ status: value || null })} />
      {shipments.isPending ? <LoadingState label="Loading shipments" />
        : shipments.isError ? <ErrorState error={shipments.error} onRetry={() => shipments.refetch()} />
        : shipments.data.items.length === 0 ? <EmptyState title="No shipments found" />
        : (
          <>
            <DataTable
              rowKey={(shipment) => shipment.id}
              rows={shipments.data.items}
              columns={[
                { header: 'Tracking', render: (shipment) => <code>{shipment.trackingCode}</code> },
                { header: 'Order', render: (shipment) => <code>{shipment.orderId}</code> },
                { header: 'Status', render: (shipment) => <StatusChip status={shipment.status} /> },
                { header: '', render: (shipment) => (
                  <Stack direction="row" spacing={1}>
                    {nextSteps[shipment.status]?.map((step) => (
                      <Button
                        key={step.status} size="small" disabled={move.isPending}
                        onClick={() => (step.status === 'DeliveryFailed' ? setFailing(shipment) : move.mutate({ id: shipment.id, next: step.status }))}
                      >
                        {step.label}
                      </Button>
                    ))}
                  </Stack>
                ) }
              ]}
            />
            <Pager page={shipments.data.page} totalPages={shipments.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
      <FormDialog
        open={failing !== null} title="Delivery failed" submitLabel="Record failure" schema={failureSchema}
        fields={[{ name: 'reason', label: 'Reason', multiline: true }]} defaults={{ reason: '' }}
        onSubmit={({ reason }) => move.mutateAsync({ id: failing!.id, next: 'DeliveryFailed', reason })} onClose={() => setFailing(null)}
      />
    </Stack>
  );
}
