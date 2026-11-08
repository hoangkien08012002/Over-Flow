import { zodResolver } from '@hookform/resolvers/zod';
import { Button, Divider, MenuItem, Stack, TextField, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { Navigate, useNavigate } from 'react-router-dom';
import { z } from 'zod';
import { api } from '../../api/client';
import { mapFieldErrors, toApiError, type ApiError } from '../../api/errors';
import { formatMoney } from '../../components/format';
import { ErrorState, LoadingState } from '../../components/PageStates';
import type { CheckoutRequest, Order, ShippingMethod } from '../../types/api';
import { createCheckoutAttempt, outcomeIsUnknown } from './checkoutAttempt';
import { useCart } from './useCart';

// The limits match the API's, which validates again.
export const checkoutSchema = z.object({
  recipientName: z.string().trim().min(1, 'Enter the recipient name.').max(150),
  phone: z.string().trim().min(1, 'Enter a phone number.').max(30).regex(/^[+\d][\d\s.-]*$/, 'Enter a valid phone number.'),
  line1: z.string().trim().min(1, 'Enter the street address.').max(300),
  ward: z.string().trim().min(1, 'Enter the ward.').max(100),
  district: z.string().trim().min(1, 'Enter the district.').max(100),
  province: z.string().trim().min(1, 'Enter the province.').max(100),
  countryCode: z.string().trim().regex(/^[A-Za-z]{2}$/, 'Use a two-letter country code.').transform((value) => value.toUpperCase()),
  shippingMethodId: z.string().min(1, 'Choose a shipping method.'),
  paymentMethod: z.enum(['COD', 'BankTransferSimulation', 'CardSimulation'], { message: 'Choose a payment method.' }),
  couponCode: z.string().trim().max(50).optional()
});
type Values = z.infer<typeof checkoutSchema>;
type Input = z.input<typeof checkoutSchema>;

const addressFields: { name: keyof Input; label: string; autoComplete: string }[] = [
  { name: 'recipientName', label: 'Recipient name', autoComplete: 'name' },
  { name: 'phone', label: 'Phone', autoComplete: 'tel' },
  { name: 'line1', label: 'Street address', autoComplete: 'address-line1' },
  { name: 'ward', label: 'Ward', autoComplete: 'address-level3' },
  { name: 'district', label: 'District', autoComplete: 'address-level2' },
  { name: 'province', label: 'Province', autoComplete: 'address-level1' },
  { name: 'countryCode', label: 'Country code', autoComplete: 'country' }
];
const fieldNames = [...addressFields.map((field) => field.name), 'shippingMethodId', 'paymentMethod', 'couponCode'] as (keyof Input)[];
const paymentLabels = { COD: 'Cash on delivery', BankTransferSimulation: 'Bank transfer (simulation)', CardSimulation: 'Card (simulation)' };

export function CheckoutPage() {
  const cart = useCart();
  const methods = useQuery({ queryKey: ['shipping-methods'], queryFn: async () => (await api.get<ShippingMethod[]>('/shipping-methods')).data });
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const attempt = useRef(createCheckoutAttempt()).current;
  const [failure, setFailure] = useState<ApiError | null>(null);
  // Set once the order exists, so the emptied cart does not redirect this page before it moves to the accepted page.
  const [placed, setPlaced] = useState(false);
  const { control, handleSubmit, setError, watch, formState: { isSubmitting } } = useForm<Input, unknown, Values>({
    resolver: zodResolver(checkoutSchema),
    defaultValues: { recipientName: '', phone: '', line1: '', ward: '', district: '', province: '', countryCode: 'VN', shippingMethodId: '', paymentMethod: 'CardSimulation', couponCode: '' }
  });
  const selectedMethod = methods.data?.find((method) => method.id === watch('shippingMethodId'));

  if (cart.isPending || methods.isPending) return <LoadingState label="Preparing checkout" />;
  if (cart.isError || methods.isError) return <ErrorState error={cart.error ?? methods.error} onRetry={() => { cart.refetch(); methods.refetch(); }} />;
  if (!placed && (!cart.data.id || cart.data.items.length === 0)) return <Navigate to="/cart" replace />;

  const submit = async ({ shippingMethodId, paymentMethod, couponCode, ...deliveryAddress }: Values) => {
    setFailure(null);
    const request: CheckoutRequest = { cartId: cart.data.id!, shippingMethodId, paymentMethod, couponCode: couponCode || null, deliveryAddress };
    try {
      const { data } = await api.post<Order>('/orders', request, { headers: { 'Idempotency-Key': attempt.keyFor(request) } });
      attempt.settled();
      setPlaced(true);
      navigate(`/orders/${data.id}/accepted`, { replace: true });
      await Promise.all([queryClient.invalidateQueries({ queryKey: ['cart'] }), queryClient.invalidateQueries({ queryKey: ['orders'] })]);
    } catch (error) {
      const problem = toApiError(error);
      attempt.settled(problem);
      const { mapped, showGlobal } = mapFieldErrors(problem, fieldNames);
      Object.entries(mapped).forEach(([field, message]) => setError(field as keyof Input, { message }));
      if (showGlobal) setFailure(problem);
    }
  };

  const currency = cart.data.items[0]?.currencyCode;
  return (
    <Stack component="form" onSubmit={handleSubmit(submit)} noValidate spacing={2} sx={{ maxWidth: 560 }}>
      <Typography variant="h5" component="h1">Checkout</Typography>
      {failure && <ErrorState error={failure} />}
      {failure && outcomeIsUnknown(failure) && (
        <Typography variant="body2" data-testid="retry-hint">We could not confirm your order. Placing it again is safe and will not create a second order.</Typography>
      )}
      <Typography variant="h6" component="h2">Delivery address</Typography>
      {addressFields.map((field) => (
        <Controller key={field.name} name={field.name} control={control} render={({ field: input, fieldState }) => (
          <TextField {...input} label={field.label} autoComplete={field.autoComplete} error={!!fieldState.error} helperText={fieldState.error?.message} />
        )} />
      ))}
      <Divider />
      <Controller name="shippingMethodId" control={control} render={({ field, fieldState }) => (
        <TextField {...field} select label="Shipping method" data-testid="shipping-field" error={!!fieldState.error} helperText={fieldState.error?.message}>
          {methods.data.map((method) => <MenuItem key={method.id} value={method.id}>{method.name} — {formatMoney(method.fee, method.currencyCode)}</MenuItem>)}
        </TextField>
      )} />
      <Controller name="paymentMethod" control={control} render={({ field, fieldState }) => (
        <TextField {...field} select label="Payment method" error={!!fieldState.error} helperText={fieldState.error?.message}>
          {Object.entries(paymentLabels).map(([value, label]) => <MenuItem key={value} value={value}>{label}</MenuItem>)}
        </TextField>
      )} />
      <Controller name="couponCode" control={control} render={({ field, fieldState }) => (
        <TextField {...field} label="Coupon code (optional)" error={!!fieldState.error} helperText={fieldState.error?.message} />
      )} />
      <Divider />
      <Typography>Items: {formatMoney(cart.data.subtotal, currency)}</Typography>
      {selectedMethod && <Typography>Shipping: {formatMoney(selectedMethod.fee, selectedMethod.currencyCode)}</Typography>}
      <Typography variant="body2" color="text.secondary">The server confirms the final total, including any coupon, when you place the order.</Typography>
      <Button type="submit" variant="contained" disabled={isSubmitting} sx={{ alignSelf: 'flex-start' }}>{isSubmitting ? 'Placing order…' : 'Place order'}</Button>
    </Stack>
  );
}
