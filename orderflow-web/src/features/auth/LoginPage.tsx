import { zodResolver } from '@hookform/resolvers/zod';
import { Button, Link, Stack, TextField, Typography } from '@mui/material';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { Navigate, Link as RouterLink, useLocation } from 'react-router-dom';
import { z } from 'zod';
import { login } from '../../api/client';
import { toApiError, type ApiError } from '../../api/errors';
import { ErrorState } from '../../components/PageStates';
import { useAuth } from './AuthProvider';

const schema = z.object({
  email: z.string().trim().min(1, 'Enter your email.').email('Enter a valid email.'),
  password: z.string().min(1, 'Enter your password.')
});
type Values = z.infer<typeof schema>;

// Only a path inside this app is followed, so a crafted link cannot send the user elsewhere after sign-in.
export const safeReturnPath = (from: unknown) => (typeof from === 'string' && from.startsWith('/') && !from.startsWith('//') ? from : '/');

export function LoginPage() {
  const { current } = useAuth();
  const location = useLocation();
  const [failure, setFailure] = useState<ApiError | null>(null);
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<Values>({ resolver: zodResolver(schema) });
  const from = safeReturnPath((location.state as { from?: unknown } | null)?.from);
  if (current) return <Navigate to={from} replace />;

  const submit = async (values: Values) => {
    setFailure(null);
    try {
      await login(values.email, values.password);
    } catch (error) {
      setFailure(toApiError(error));
    }
  };

  return (
    <Stack component="form" onSubmit={handleSubmit(submit)} noValidate spacing={2} sx={{ maxWidth: 400, mx: 'auto', mt: 4 }}>
      <Typography variant="h5" component="h1">Sign in</Typography>
      {failure && <ErrorState error={failure} />}
      <TextField label="Email" type="email" autoComplete="email" autoFocus error={!!errors.email} helperText={errors.email?.message} {...register('email')} />
      <TextField label="Password" type="password" autoComplete="current-password" error={!!errors.password} helperText={errors.password?.message} {...register('password')} />
      <Button type="submit" variant="contained" disabled={isSubmitting}>{isSubmitting ? 'Signing in…' : 'Sign in'}</Button>
      <Typography variant="body2">No account yet? <Link component={RouterLink} to="/register">Create one</Link></Typography>
    </Stack>
  );
}
