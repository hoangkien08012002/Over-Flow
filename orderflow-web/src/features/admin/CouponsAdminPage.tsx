import { Alert, Button, Stack, Typography } from '@mui/material';
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { z } from 'zod';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { FormDialog } from '../../components/FormDialog';
import { formatDate, formatMoney, toLocalInput } from '../../components/format';
import { Pager, useListParams } from '../../components/ListControls';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { Coupon, CouponUsage, Paged } from '../../types/api';

const limit = z.preprocess((value) => (value === '' || value === null || value === undefined ? null : Number(value)), z.number().int('Use a whole number.').min(1, 'Use 1 or more, or leave empty.').nullable());
const instant = z.string().min(1, 'Choose a date and time.').transform((value) => new Date(value).toISOString());

const schema = z.object({
  code: z.string().trim().min(1, 'Enter a code.').max(50),
  discountType: z.enum(['Fixed', 'Percentage']),
  discountValue: z.coerce.number({ message: 'Enter a value.' }).positive('Use a value above zero.'),
  minimumSubtotal: z.coerce.number({ message: 'Enter an amount.' }).min(0, 'Use zero or more.'),
  validFromUtc: instant,
  validUntilUtc: instant,
  totalUsageLimit: limit,
  perUserUsageLimit: limit,
  isEnabled: z.boolean()
}).refine((value) => value.discountType === 'Fixed' || value.discountValue <= 100, { path: ['discountValue'], message: 'A percentage cannot exceed 100.' })
  .refine((value) => value.validFromUtc < value.validUntilUtc, { path: ['validUntilUtc'], message: 'This must be after the start.' });

const blank = () => ({
  code: '', discountType: 'Percentage', discountValue: 10, minimumSubtotal: 0, validFromUtc: toLocalInput(new Date()), validUntilUtc: toLocalInput(new Date(Date.now() + 30 * 86_400_000)),
  totalUsageLimit: '', perUserUsageLimit: '', isEnabled: true
});

export function CouponsAdminPage() {
  const { page, update } = useListParams();
  const [editing, setEditing] = useState<Coupon | 'new' | null>(null);
  const [usageOf, setUsageOf] = useState<string | null>(null);
  const coupons = useQuery({
    queryKey: ['coupons', page],
    staleTime: 0,
    placeholderData: keepPreviousData,
    queryFn: async () => (await api.get<Paged<Coupon>>('/admin/coupons', { params: { page } })).data
  });
  const usage = useQuery({ queryKey: ['coupons', 'usage', usageOf], enabled: usageOf !== null, staleTime: 0, queryFn: async () => (await api.get<CouponUsage>(`/admin/coupons/${usageOf}`)).data });
  const save = useAction((values: z.infer<typeof schema>) => {
    const coupon = editing === 'new' ? null : editing;
    return coupon ? api.put(`/admin/coupons/${coupon.id}`, { ...values, version: coupon.version }) : api.post('/admin/coupons', values);
  }, [['coupons']]);
  const toggle = useAction((coupon: Coupon) => api.put(`/admin/coupons/${coupon.id}/activation`, { isEnabled: !coupon.isEnabled, version: coupon.version }), [['coupons']]);

  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Coupons</Typography>
      <Button variant="contained" sx={{ alignSelf: 'flex-start' }} onClick={() => setEditing('new')}>New coupon</Button>
      {toggle.error && <ErrorState error={toggle.error} />}
      {usage.isError && <ErrorState error={usage.error} />}
      {usage.data && (
        <Alert severity="info" onClose={() => setUsageOf(null)} data-testid="coupon-usage">
          {usage.data.coupon.code}: {usage.data.activeUsages} active uses, {usage.data.returnedUsages} returned, {formatMoney(usage.data.discountGiven)} given in discounts.
        </Alert>
      )}
      {coupons.isPending ? <LoadingState label="Loading coupons" />
        : coupons.isError ? <ErrorState error={coupons.error} onRetry={() => coupons.refetch()} />
        : coupons.data.items.length === 0 ? <EmptyState title="No coupons yet" />
        : (
          <>
            <DataTable
              rowKey={(coupon) => coupon.id}
              rows={coupons.data.items}
              columns={[
                { header: 'Code', render: (coupon) => coupon.code },
                { header: 'Discount', render: (coupon) => (coupon.discountType === 'Percentage' ? `${coupon.discountValue}%` : formatMoney(coupon.discountValue)) },
                { header: 'Valid until', render: (coupon) => formatDate(coupon.validUntilUtc) },
                { header: 'Used', align: 'right', render: (coupon) => coupon.usageCount },
                { header: 'Status', render: (coupon) => (coupon.isEnabled ? 'Enabled' : 'Disabled') },
                { header: '', render: (coupon) => (
                  <Stack direction="row" spacing={1}>
                    <Button size="small" aria-label={`Edit ${coupon.code}`} onClick={() => setEditing(coupon)}>Edit</Button>
                    <Button size="small" aria-label={`Usage of ${coupon.code}`} onClick={() => setUsageOf(coupon.id)}>Usage</Button>
                    <Button size="small" aria-label={`${coupon.isEnabled ? 'Disable' : 'Enable'} ${coupon.code}`} onClick={() => toggle.mutate(coupon)}>{coupon.isEnabled ? 'Disable' : 'Enable'}</Button>
                  </Stack>
                ) }
              ]}
            />
            <Pager page={coupons.data.page} totalPages={coupons.data.totalPages} onChange={(next) => update({ page: String(next) })} />
          </>
        )}
      <FormDialog
        open={editing !== null}
        title={editing === 'new' ? 'New coupon' : 'Edit coupon'}
        schema={schema}
        defaults={editing && editing !== 'new'
          ? { ...editing, validFromUtc: toLocalInput(editing.validFromUtc), validUntilUtc: toLocalInput(editing.validUntilUtc), totalUsageLimit: editing.totalUsageLimit ?? '', perUserUsageLimit: editing.perUserUsageLimit ?? '' }
          : blank()}
        fields={[
          { name: 'code', label: 'Code', disabled: editing !== 'new' },
          { name: 'discountType', label: 'Discount type', type: 'select', options: [{ value: 'Percentage', label: 'Percentage' }, { value: 'Fixed', label: 'Fixed amount' }] },
          { name: 'discountValue', label: 'Discount value', type: 'number' },
          { name: 'minimumSubtotal', label: 'Minimum subtotal', type: 'number' },
          { name: 'validFromUtc', label: 'Valid from', type: 'datetime-local' },
          { name: 'validUntilUtc', label: 'Valid until', type: 'datetime-local' },
          { name: 'totalUsageLimit', label: 'Total usage limit (optional)', type: 'number' },
          { name: 'perUserUsageLimit', label: 'Per-customer limit (optional)', type: 'number' },
          { name: 'isEnabled', label: 'Enabled', type: 'checkbox' }
        ]}
        onSubmit={(values) => save.mutateAsync(values)}
        onClose={() => setEditing(null)}
      />
    </Stack>
  );
}
