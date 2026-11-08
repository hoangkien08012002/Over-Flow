import { Alert, Button, Divider, List, ListItem, ListItemText, Rating, Stack, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useLocation, useNavigate, useParams } from 'react-router-dom';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import { formatDate, formatMoney } from '../../components/format';
import { Pager } from '../../components/ListControls';
import { ErrorState, LoadingState } from '../../components/PageStates';
import type { Product, ReviewList, WishlistItem } from '../../types/api';
import { useAuth } from '../auth/AuthProvider';
import { useAddToCart } from '../cart/useCart';

function Reviews({ productId }: { productId: string }) {
  const [page, setPage] = useState(1);
  const reviews = useQuery({
    queryKey: ['reviews', productId, page],
    staleTime: 0,
    queryFn: async () => (await api.get<ReviewList>(`/products/${productId}/reviews`, { params: { page, pageSize: 10 } })).data
  });
  if (reviews.isPending) return <LoadingState label="Loading reviews" />;
  if (reviews.isError) return <ErrorState error={reviews.error} onRetry={() => reviews.refetch()} />;
  const { summary, items, totalPages } = reviews.data;
  return (
    <Stack spacing={1}>
      <Typography variant="h6" component="h2">Reviews</Typography>
      {summary.count === 0 ? <Typography color="text.secondary">No reviews yet.</Typography> : (
        <>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }} data-testid="review-summary">
            <Rating value={summary.averageRating} precision={0.1} readOnly />
            <Typography>{summary.averageRating} out of 5 from {summary.count}</Typography>
          </Stack>
          <List>
            {items.map((review) => (
              <ListItem key={review.id} divider>
                <ListItemText primary={`${review.authorName} · ${review.rating}/5`} secondary={`${review.comment ?? ''} (${formatDate(review.createdAtUtc)})`} />
              </ListItem>
            ))}
          </List>
          <Pager page={page} totalPages={totalPages} onChange={setPage} />
        </>
      )}
    </Stack>
  );
}

export function ProductDetailPage() {
  const { id = '' } = useParams();
  const { current, hasRole } = useAuth();
  const navigate = useNavigate();
  const location = useLocation();
  const [quantity, setQuantity] = useState('1');
  const isCustomer = hasRole('Customer');
  const product = useQuery({ queryKey: ['product', id], staleTime: 0, queryFn: async () => (await api.get<Product>(`/products/${id}`)).data });
  const wishlist = useQuery({ queryKey: ['wishlist'], enabled: isCustomer, queryFn: async () => (await api.get<{ items: WishlistItem[] }>('/wishlist')).data });
  const add = useAddToCart();
  const listed = wishlist.data?.items.some((item) => item.productId === id) ?? false;
  const toggleWishlist = useAction(() => (listed ? api.delete(`/wishlist/${id}`) : api.put(`/wishlist/${id}`)), [['wishlist']]);

  if (product.isPending) return <LoadingState label="Loading the product" />;
  if (product.isError) return <ErrorState error={product.error} onRetry={() => product.refetch()} />;
  const item = product.data;
  const count = Number(quantity);
  const quantityValid = Number.isInteger(count) && count >= 1 && count <= 100;

  return (
    <Stack spacing={2} sx={{ maxWidth: 720 }}>
      <Typography variant="h5" component="h1">{item.name}</Typography>
      <Typography color="text.secondary">{item.categoryName} · SKU {item.sku}</Typography>
      <Typography variant="h6" data-testid="product-price">{formatMoney(item.price, item.currencyCode)}</Typography>
      <Typography data-testid="product-available">{item.available > 0 ? `${item.available} available` : 'Out of stock'}</Typography>
      {item.description && <Typography>{item.description}</Typography>}
      {(add.error || toggleWishlist.error) && <ErrorState error={add.error ?? toggleWishlist.error} />}
      {add.isSuccess && <Alert severity="success">Added to your cart.</Alert>}
      <Stack direction="row" useFlexGap spacing={1} sx={{ flexWrap: 'wrap', alignItems: 'flex-start' }}>
        <TextField
          size="small" type="number" label="Quantity" value={quantity} sx={{ width: 110 }} error={!quantityValid} helperText={quantityValid ? undefined : '1 to 100'}
          slotProps={{ htmlInput: { min: 1, max: 100 } }} onChange={(event) => setQuantity(event.target.value)}
        />
        {!current && <Button variant="contained" onClick={() => navigate('/login', { state: { from: location.pathname } })}>Sign in to buy</Button>}
        {isCustomer && <Button variant="contained" disabled={!quantityValid || add.isPending} onClick={() => add.mutate({ productId: id, quantity: count })}>Add to cart</Button>}
        {isCustomer && <Button variant="outlined" disabled={toggleWishlist.isPending || wishlist.isPending} onClick={() => toggleWishlist.mutate()}>{listed ? 'Remove from wishlist' : 'Add to wishlist'}</Button>}
      </Stack>
      <Divider />
      <Reviews productId={id} />
    </Stack>
  );
}
