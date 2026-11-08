import { Button, Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { z } from 'zod';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { FormDialog } from '../../components/FormDialog';
import { formatMoney } from '../../components/format';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { ShippingMethod } from '../../types/api';

const schema = z.object({
  code: z.string().trim().min(1, 'Enter a code.').max(40),
  name: z.string().trim().min(1, 'Enter a name.').max(150),
  fee: z.coerce.number({ message: 'Enter a fee.' }).min(0, 'A fee cannot be negative.').multipleOf(0.01, 'Use at most two decimal places.'),
  currencyCode: z.string().trim().regex(/^[A-Z]{3}$/, 'Use a three-letter code such as VND.'),
  isActive: z.boolean()
});

export function ShippingMethodsAdminPage() {
  const [editing, setEditing] = useState<ShippingMethod | 'new' | null>(null);
  const methods = useQuery({ queryKey: ['admin-shipping-methods'], staleTime: 0, queryFn: async () => (await api.get<ShippingMethod[]>('/admin/shipping-methods')).data });
  const save = useAction((values: z.infer<typeof schema>) => {
    const method = editing === 'new' ? null : editing;
    return method ? api.put(`/admin/shipping-methods/${method.id}`, { ...values, version: method.version }) : api.post('/admin/shipping-methods', values);
  }, [['admin-shipping-methods'], ['shipping-methods']]);

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Shipping methods</Typography>
      <Button variant="contained" sx={{ alignSelf: 'flex-start' }} onClick={() => setEditing('new')}>New shipping method</Button>
      {methods.isPending ? <LoadingState label="Loading shipping methods" />
        : methods.isError ? <ErrorState error={methods.error} onRetry={() => methods.refetch()} />
        : methods.data.length === 0 ? <EmptyState title="No shipping methods yet" />
        : (
          <DataTable
            rowKey={(method) => method.id}
            rows={methods.data}
            columns={[
              { header: 'Code', render: (method) => method.code },
              { header: 'Name', render: (method) => method.name },
              { header: 'Fee', align: 'right', render: (method) => formatMoney(method.fee, method.currencyCode) },
              { header: 'Status', render: (method) => (method.isActive ? 'Active' : 'Inactive') },
              { header: '', render: (method) => <Button size="small" aria-label={`Edit ${method.code}`} onClick={() => setEditing(method)}>Edit</Button> }
            ]}
          />
        )}
      <FormDialog
        open={editing !== null}
        title={editing === 'new' ? 'New shipping method' : 'Edit shipping method'}
        schema={schema}
        defaults={editing && editing !== 'new' ? { ...editing } : { code: '', name: '', fee: '', currencyCode: 'VND', isActive: true }}
        fields={[
          { name: 'code', label: 'Code', disabled: editing !== 'new' },
          { name: 'name', label: 'Name' },
          { name: 'fee', label: 'Fee', type: 'number', helperText: 'Past orders keep the fee they were placed with.' },
          { name: 'currencyCode', label: 'Currency' },
          { name: 'isActive', label: 'Active', type: 'checkbox' }
        ]}
        onSubmit={(values) => save.mutateAsync(values)}
        onClose={() => setEditing(null)}
      />
    </Stack>
  );
}
