export function sumVnd(amounts: (string | null | undefined)[]): string {
  let total = 0n;
  for (const amt of amounts) {
    if (amt != null && amt.trim() !== '') {
      total += BigInt(amt.trim());
    }
  }
  return total.toString();
}

export function formatVnd(amount: string | number | null | undefined): string {
  if (amount == null) return '—';
  const str = typeof amount === 'number' ? Math.round(amount).toString() : amount.trim();
  if (str === '') return '—';
  const bi = BigInt(str);
  const isNegative = bi < 0n;
  const absStr = (isNegative ? -bi : bi).toString();
  const formatted = absStr.replace(/\B(?=(\d{3})+(?!\d))/g, '.');
  return `${isNegative ? '-' : ''}${formatted} ₫`;
}

export const formatMoney = (value: number | string | null | undefined): string => formatVnd(value);
