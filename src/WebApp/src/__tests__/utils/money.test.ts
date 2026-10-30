import { describe, it, expect } from 'vitest';
import { formatVnd, sumVnd } from '../../utils/money';

describe('money', () => {
  it('sumVnd preserves whole-unit precision across large numbers', () => {
    expect(sumVnd(['999999999999999999', '1'])).toBe('1000000000000000000');
  });

  it('sumVnd handles multiple numbers and nullish values', () => {
    expect(sumVnd(['50000', null, '25000', undefined, '0'])).toBe('75000');
  });

  it('formatVnd formats zero and standard fares', () => {
    expect(formatVnd('0')).toBe('0 ₫');
    expect(formatVnd('50000')).toBe('50.000 ₫');
  });

  it('formatVnd handles large numbers without precision loss', () => {
    expect(formatVnd('999999999999999999')).toBe('999.999.999.999.999.999 ₫');
  });

  it('formatVnd returns em-dash for null or empty string', () => {
    expect(formatVnd(null)).toBe('—');
    expect(formatVnd(undefined)).toBe('—');
    expect(formatVnd('')).toBe('—');
  });
});
