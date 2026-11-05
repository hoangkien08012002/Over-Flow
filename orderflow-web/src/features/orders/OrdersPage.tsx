import { List, ListItem, ListItemText, Stack, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { EmptyState, ErrorState, LoadingState } from '../../components/PageStates';
import type { Order, Paged } from '../../types/api';

export function OrdersPage() {
  const query = useQuery({ queryKey: ['orders'], queryFn: async () => (await api.get<Paged<Order>>('/orders')).data });
  if (query.isPending) return <LoadingState label="Loading your orders" />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => query.refetch()} />;
  if (query.data.items.length === 0) return <EmptyState title="You have no orders yet" />;
  return (
    <Stack spacing={2}>
      <Typography variant="h5" component="h1">My orders</Typography>
      <List>
        {query.data.items.map((order) => (
          <ListItem key={order.id} divider>
            <ListItemText primary={order.orderNumber} secondary={`${order.status} · ${order.grandTotal.toLocaleString()} ${order.currencyCode}`} />
          </ListItem>
        ))}
      </List>
    </Stack>
  );
}
