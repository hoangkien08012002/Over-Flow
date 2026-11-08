import { useMutation, useQueryClient, type QueryKey } from '@tanstack/react-query';

// A customer or staff action. It refreshes what it changed even after a failure, since a 409 means the page was out of date, and the failure stays on the mutation for the page to show.
export function useAction<V = void>(action: (variables: V) => Promise<unknown>, invalidate: QueryKey[]) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: action,
    onSettled: () => Promise.all(invalidate.map((queryKey) => queryClient.invalidateQueries({ queryKey })))
  });
}
