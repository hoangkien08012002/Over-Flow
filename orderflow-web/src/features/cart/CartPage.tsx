import { Alert, Button, Link, Stack, TextField, Typography } from '@mui/material';
import { useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { formatMoney } from '../../components/format';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { Cart, CartLine } from '../../types/api';
import { useCart } from './useCart';

function QuantityInput({ line, onCommit }: { line: CartLine; onCommit: (quantity: number) => void }) {
  const [draft, setDraft] = useState<string | null>(null);
  const value = draft ?? String(line.quantity);
  const parsed = Number(value);
  const valid = Number.isInteger(parsed) && parsed >= 1 && parsed <= 100;
  return (
    <TextField
      type="number"
      size="small"
      value={value}
      error={!valid}
      helperText={valid ? undefined : '1 to 100'}
      sx={{ width: 100 }}
      slotProps={{ htmlInput: { min: 1, max: 100, 'aria-label': `Quantity of ${line.productName}` } }}
      onChange={(event) => setDraft(event.target.value)}
      onBlur={() => {
        if (valid && parsed !== line.quantity) onCommit(parsed);
        if (valid) setDraft(null);
      }}
    />
  );
}

export function CartPage() {
  const cart = useCart();
  const update = useAction(({ id, quantity }: { id: string; quantity: number }) => api.put<Cart>(`/cart/items/${id}`, { quantity }), [['cart']]);
  const remove = useAction((id: string) => api.delete(`/cart/items/${id}`), [['cart']]);

  if (cart.isPending) return <LoadingState label="Loading your cart" />;
  if (cart.isError) return <ErrorState error={cart.error} onRetry={() => cart.refetch()} />;
  if (cart.data.items.length === 0) return <EmptyState title="Your cart is empty" hint="Add a product to get started." />;

  const blocked = cart.data.items.some((line) => line.unavailable);
  const currency = cart.data.items[0]?.currencyCode;
  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Cart</Typography>
      {(update.error || remove.error) && <ErrorState error={update.error ?? remove.error} />}
      {blocked && <Alert severity="warning">An item below is no longer available in that quantity. Change or remove it to continue.</Alert>}
      <DataTable
        rowKey={(line) => line.id}
        rows={cart.data.items}
        columns={[
          { header: 'Product', render: (line) => (
            <>
              <Link component={RouterLink} to={`/products/${line.productId}`}>{line.productName ?? 'Product no longer listed'}</Link>
              {line.unavailable && <Typography variant="body2" color="error">Only {line.available} available</Typography>}
            </>
          ) },
          { header: 'Current price', align: 'right', render: (line) => formatMoney(line.currentUnitPrice, line.currencyCode) },
          { header: 'Quantity', render: (line) => <QuantityInput line={line} onCommit={(quantity) => update.mutate({ id: line.id, quantity })} /> },
          { header: 'Subtotal', align: 'right', render: (line) => formatMoney(line.lineSubtotal, line.currencyCode) },
          { header: '', render: (line) => <Button size="small" aria-label={`Remove ${line.productName}`} onClick={() => remove.mutate(line.id)}>Remove</Button> }
        ]}
      />
      <Typography variant="h6" data-testid="cart-subtotal">Subtotal: {formatMoney(cart.data.subtotal, currency)}</Typography>
      <Typography variant="body2" color="text.secondary">Prices are current list prices. The final total is calculated when you place the order.</Typography>
      <Button variant="contained" component={RouterLink} to="/checkout" disabled={blocked} sx={{ alignSelf: 'flex-start' }}>Go to checkout</Button>
    </Stack>
  );
}
