import { zodResolver } from '@hookform/resolvers/zod';
import { Button, Link, Stack, TextField, Typography } from '@mui/material';
import { useState } from 'react';
import { useForm } from 'react-hook-form';
import { Link as RouterLink, useNavigate } from 'react-router-dom';
import { z } from 'zod';
import { login, register as registerAccount } from '../../api/client';
import { toApiError, type ApiError } from '../../api/errors';
import { ErrorState } from '../../components/PageStates';

const schema = z.object({
  firstName: z.string().trim().min(1, 'Enter your first name.').max(100),
  lastName: z.string().trim().min(1, 'Enter your last name.').max(100),
  email: z.string().trim().min(1, 'Enter your email.').email('Enter a valid email.'),
  password: z.string()
    .min(12, 'Use at least 12 characters.')
    .regex(/[a-z]/, 'Include a lowercase letter.')
    .regex(/[A-Z]/, 'Include an uppercase letter.')
    .regex(/\d/, 'Include a number.')
    .regex(/[^a-zA-Z0-9]/, 'Include a symbol.')
});
type Values = z.infer<typeof schema>;

export function RegisterPage() {
  const navigate = useNavigate();
  const [failure, setFailure] = useState<ApiError | null>(null);
  const { register, handleSubmit, formState: { errors, isSubmitting } } = useForm<Values>({ resolver: zodResolver(schema) });

  const submit = async (values: Values) => {
    setFailure(null);
    try {
      await registerAccount(values);
      await login(values.email, values.password);
      navigate('/', { replace: true });
    } catch (error) {
      setFailure(toApiError(error));
    }
  };

  return (
    <Stack component="form" onSubmit={handleSubmit(submit)} noValidate spacing={2} sx={{ maxWidth: 400, mx: 'auto', mt: 4 }}>
      <Typography variant="h5" component="h1">Create an account</Typography>
      {failure && <ErrorState error={failure} />}
      <TextField label="First name" autoComplete="given-name" autoFocus error={!!errors.firstName} helperText={errors.firstName?.message} {...register('firstName')} />
      <TextField label="Last name" autoComplete="family-name" error={!!errors.lastName} helperText={errors.lastName?.message} {...register('lastName')} />
      <TextField label="Email" type="email" autoComplete="email" error={!!errors.email} helperText={errors.email?.message} {...register('email')} />
      <TextField label="Password" type="password" autoComplete="new-password" error={!!errors.password} helperText={errors.password?.message ?? 'At least 12 characters with upper and lower case, a number, and a symbol.'} {...register('password')} />
      <Button type="submit" variant="contained" disabled={isSubmitting}>{isSubmitting ? 'Creating…' : 'Create account'}</Button>
      <Typography variant="body2">Already registered? <Link component={RouterLink} to="/login">Sign in</Link></Typography>
    </Stack>
  );
}
