import { Button, Link, Stack, TextField, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link as RouterLink, useParams } from 'react-router-dom';
import { z } from 'zod';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { FormDialog } from '../../components/FormDialog';
import { formatDate } from '../../components/format';
import { Pager, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import { StatusChip } from '../../components/StatusChip';
import type { InventorySnapshot, Paged, Product, Reservation } from '../../types/api';

export function InventoryListPage() {
  const { params, page, get, update } = useListParams();
  const [search, setSearch] = useState(get('search'));
  const products = useQuery({
    queryKey: ['products', 'inventory', params.toString()],
    staleTime: 0,
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.get<Paged<Product>>('/products', { params: { search: get('search') || undefined, page } })).data
  });
  const applySearch = (event: FormEvent) => { event.preventDefault(); update({ search: search.trim() || null }); };
  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Inventory</Typography>
      <form onSubmit={applySearch}><TextField size="small" label="Search" value={search} onChange={(event) => setSearch(event.target.value)} /></form>
      {products.isPending ? <LoadingState label="Loading products" />
        : products.isError ? <ErrorState error={products.error} onRetry={() => products.refetch()} />
        : products.data.items.length === 0 ? <EmptyState title="No products found" />
        : (
          <>
            <DataTable
              rowKey={(product) => product.id}
              rows={products.data.items}
              columns={[
                { header: 'Product', render: (product) => <Link component={RouterLink} to={`/admin/inventory/${product.id}`}>{product.name}</Link> },
                { header: 'SKU', render: (product) => product.sku },
                { header: 'Available', align: 'right', render: (product) => product.available }
              ]}
            />
            <Pager page={products.data.page} totalPages={products.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
    </Stack>
  );
}

const adjustSchema = z.object({
  quantityDelta: z.coerce.number({ message: 'Enter a quantity.' }).int('Use a whole number.').refine((value) => value !== 0, 'Enter a change other than zero.'),
  reasonCode: z.string().trim().min(1, 'Enter a reason code.').max(50),
  note: z.string().trim().max(500).optional()
});
const thresholdSchema = z.object({ lowStockThreshold: z.coerce.number({ message: 'Enter a number.' }).int('Use a whole number.').min(0, 'Use zero or more.') });

export function InventoryDetailPage() {
  const { productId = '' } = useParams();
  const [dialog, setDialog] = useState<'adjust' | 'threshold' | null>(null);
  const keys = [['inventory', productId], ['products']];
  const product = useQuery({ queryKey: ['product', productId], queryFn: async () => (await api.get<Product>(`/products/${productId}`)).data });
  const stock = useQuery({ queryKey: ['inventory', productId, 'stock'], staleTime: 0, queryFn: async () => (await api.get<InventorySnapshot>(`/inventory/${productId}`)).data });
  const reservations = useQuery({ queryKey: ['inventory', productId, 'reservations'], staleTime: 0, queryFn: async () => (await api.get<Reservation[]>(`/inventory/${productId}/reservations`)).data });
  const adjust = useAction((values: z.infer<typeof adjustSchema>) => api.post(`/inventory/${productId}/adjust`, values), keys);
  const threshold = useAction((values: z.infer<typeof thresholdSchema>) => api.put(`/inventory/${productId}/threshold`, values), keys);

  if (stock.isPending) return <LoadingState label="Loading stock" />;
  if (stock.isError) return <ErrorState error={stock.error} onRetry={() => stock.refetch()} />;
  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Inventory: {product.data?.name ?? productId}</Typography>
      <Typography data-testid="stock">On hand {stock.data.onHand} · Reserved {stock.data.reserved} · Available {stock.data.available} · Low-stock threshold {stock.data.lowStockThreshold}</Typography>
      <Stack direction="row" spacing={1}>
        <Button variant="contained" onClick={() => setDialog('adjust')}>Adjust stock</Button>
        <Button variant="outlined" onClick={() => setDialog('threshold')}>Set threshold</Button>
      </Stack>
      <Typography variant="h6" component="h2">Recent reservations</Typography>
      {reservations.isError ? <ErrorState error={reservations.error} onRetry={() => reservations.refetch()} />
        : reservations.data?.length === 0 ? <EmptyState title="No reservations yet" />
        : reservations.data && (
          <DataTable
            rowKey={(reservation) => reservation.id}
            rows={reservations.data}
            columns={[
              { header: 'Order', render: (reservation) => <code>{reservation.orderId}</code> },
              { header: 'Quantity', align: 'right', render: (reservation) => reservation.quantity },
              { header: 'Status', render: (reservation) => <StatusChip status={reservation.status} /> },
              { header: 'Expires', render: (reservation) => formatDate(reservation.expiresAtUtc) },
              { header: '', render: (reservation) => reservation.status === 'Committed' && (
                <Button size="small" component={RouterLink} to={`/admin/shipments?orderId=${reservation.orderId}`}>Ship</Button>
              ) }
            ]}
          />
        )}
      <FormDialog
        open={dialog === 'adjust'} title="Adjust stock" submitLabel="Adjust" schema={adjustSchema}
        defaults={{ quantityDelta: '', reasonCode: 'PURCHASE_RECEIPT', note: '' }}
        fields={[
          { name: 'quantityDelta', label: 'Quantity change', type: 'number', helperText: 'Negative to remove stock.' },
          { name: 'reasonCode', label: 'Reason code' },
          { name: 'note', label: 'Note (optional)', multiline: true }
        ]}
        onSubmit={(values) => adjust.mutateAsync(values)} onClose={() => setDialog(null)}
      />
      <FormDialog
        open={dialog === 'threshold'} title="Low-stock threshold" schema={thresholdSchema}
        defaults={{ lowStockThreshold: stock.data.lowStockThreshold }}
        fields={[{ name: 'lowStockThreshold', label: 'Threshold', type: 'number' }]}
        onSubmit={(values) => threshold.mutateAsync(values)} onClose={() => setDialog(null)}
      />
    </Stack>
  );
}
