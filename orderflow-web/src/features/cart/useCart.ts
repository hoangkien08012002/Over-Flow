import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { useAction } from '../../api/hooks';
import type { Cart } from '../../types/api';
import { useAuth } from '../auth/AuthProvider';

export function useCart() {
  const { hasRole } = useAuth();
  return useQuery({
    queryKey: ['cart'],
    enabled: hasRole('Customer'),
    // Prices and availability change on the server, so the cart is always read fresh.
    staleTime: 0,
    queryFn: async () => (await api.get<Cart>('/cart')).data
  });
}

export const useAddToCart = () =>
  useAction(({ productId, quantity }: { productId: string; quantity: number }) => api.post<Cart>('/cart/items', { productId, quantity }), [['cart']]);
