import { zodResolver } from '@hookform/resolvers/zod';
import { Button, Checkbox, Dialog, DialogActions, DialogContent, DialogTitle, FormControlLabel, MenuItem, Stack, TextField } from '@mui/material';
import { useEffect, useState } from 'react';
import { Controller, useForm, type FieldValues, type Path, type Resolver } from 'react-hook-form';
import type { z } from 'zod';
import { mapFieldErrors, toApiError, type ApiError } from '../api/errors';
import { ErrorState } from './PageStates';

export interface FormField {
  name: string;
  label: string;
  type?: 'text' | 'number' | 'datetime-local' | 'checkbox' | 'select';
  options?: { value: string; label: string }[];
  multiline?: boolean;
  disabled?: boolean;
  helperText?: string;
}

interface Props<T extends FieldValues> {
  open: boolean;
  title: string;
  submitLabel?: string;
  schema: z.ZodType<T>;
  fields: FormField[];
  defaults: Record<string, unknown>;
  onSubmit: (values: T) => Promise<unknown>;
  onClose: () => void;
}

// One dialog serves the short forms: it validates with the schema, then maps API field errors back onto the fields.
export function FormDialog<T extends FieldValues>({ open, title, submitLabel = 'Save', schema, fields, defaults, onSubmit, onClose }: Props<T>) {
  const [failure, setFailure] = useState<ApiError | null>(null);
  const { control, handleSubmit, reset, setError, formState: { isSubmitting } } = useForm<FieldValues, unknown, T>({
    // The form holds what inputs produce, such as text in a number field, and the schema coerces it to T.
    resolver: zodResolver(schema as never) as unknown as Resolver<FieldValues, unknown, T>,
    defaultValues: defaults
  });

  // The parent rebuilds its defaults on each render, so the form resets only when it opens.
  useEffect(() => {
    if (open) { reset(defaults); setFailure(null); }
  }, [open]);

  const submit = async (values: T) => {
    setFailure(null);
    try {
      await onSubmit(values);
      onClose();
    } catch (error) {
      const problem = toApiError(error);
      const { mapped, showGlobal } = mapFieldErrors(problem, fields.map((field) => field.name));
      Object.entries(mapped).forEach(([field, message]) => setError(field as Path<FieldValues>, { message }));
      if (showGlobal) setFailure(problem);
    }
  };

  return (
    <Dialog open={open} onClose={onClose} fullWidth maxWidth="sm">
      <form onSubmit={handleSubmit(submit)} noValidate>
        <DialogTitle>{title}</DialogTitle>
        <DialogContent>
          <Stack spacing={2} sx={{ pt: 1 }}>
            {failure && <ErrorState error={failure} />}
            {fields.map((field) => (
              <Controller
                key={field.name}
                name={field.name}
                control={control}
                render={({ field: input, fieldState }) => field.type === 'checkbox' ? (
                  <FormControlLabel label={field.label} control={<Checkbox checked={!!input.value} onChange={(event) => input.onChange(event.target.checked)} disabled={field.disabled} />} />
                ) : (
                  <TextField
                    {...input}
                    value={input.value ?? ''}
                    label={field.label}
                    type={field.type === 'select' ? undefined : field.type ?? 'text'}
                    select={field.type === 'select'}
                    multiline={field.multiline}
                    minRows={field.multiline ? 2 : undefined}
                    disabled={field.disabled}
                    error={!!fieldState.error}
                    helperText={fieldState.error?.message ?? field.helperText}
                    slotProps={field.type === 'datetime-local' ? { inputLabel: { shrink: true } } : undefined}
                  >
                    {field.options?.map((option) => <MenuItem key={option.value} value={option.value}>{option.label}</MenuItem>)}
                  </TextField>
                )}
              />
            ))}
          </Stack>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>Close</Button>
          <Button type="submit" variant="contained" disabled={isSubmitting}>{isSubmitting ? 'Working…' : submitLabel}</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
