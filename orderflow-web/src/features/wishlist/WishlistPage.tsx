import { Button, Link, Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { Link as RouterLink } from 'react-router-dom';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { DataTable } from '../../components/DataTable';
import { formatMoney } from '../../components/format';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { WishlistItem } from '../../types/api';
import { useAddToCart } from '../cart/useCart';

export function WishlistPage() {
  const query = useQuery({ queryKey: ['wishlist'], queryFn: async () => (await api.get<{ items: WishlistItem[] }>('/wishlist')).data });
  const remove = useAction((productId: string) => api.delete(`/wishlist/${productId}`), [['wishlist']]);
  const add = useAddToCart();
  if (query.isPending) return <LoadingState label="Loading your wishlist" />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => query.refetch()} />;
  if (query.data.items.length === 0) return <EmptyState title="Your wishlist is empty" hint="Save a product from its page to find it here." />;
  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">Wishlist</Typography>
      {(remove.error || add.error) && <ErrorState error={remove.error ?? add.error} />}
      <DataTable
        rowKey={(item) => item.productId}
        rows={query.data.items}
        columns={[
          { header: 'Product', render: (item) => <Link component={RouterLink} to={`/products/${item.productId}`}>{item.name}</Link> },
          { header: 'Price', align: 'right', render: (item) => formatMoney(item.price, item.currencyCode) },
          { header: 'Availability', render: (item) => (item.isAvailable ? 'In stock' : 'Unavailable') },
          { header: '', render: (item) => (
            <Stack direction="row" spacing={1}>
              <Button size="small" disabled={!item.isAvailable} onClick={() => add.mutate({ productId: item.productId, quantity: 1 })}>Add to cart</Button>
              <Button size="small" onClick={() => remove.mutate(item.productId)}>Remove</Button>
            </Stack>
          ) }
        ]}
      />
    </Stack>
  );
}
