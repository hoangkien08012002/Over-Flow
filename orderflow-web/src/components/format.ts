export const formatMoney = (amount: number, currency?: string | null) =>
  `${amount.toLocaleString('en-US', { maximumFractionDigits: 2 })}${currency ? ` ${currency}` : ''}`;

export const formatDate = (utc: string) => new Date(utc).toLocaleString();

// A date-time input works in local time without an offset, while the API takes and returns UTC.
export const toLocalInput = (utc: string | Date) => {
  const date = new Date(utc);
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 16);
};
