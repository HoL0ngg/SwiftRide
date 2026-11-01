import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import LandingPage, { legacyRoleRedirect } from '../../apps/landing/LandingPage';
import { initialsOf } from '../../components/ui/Avatar';
import TripHistoryList from '../../components/ui/TripHistoryList';
import type { TripResponse } from '../../types/tripTypes';

describe('landing page', () => {
  it('offers one entry to each dedicated app', () => {
    const html = renderToStaticMarkup(<LandingPage />);
    expect(html).toMatch(/<a[^>]+href="\/rider\/"/);
    expect(html).toMatch(/<a[^>]+href="\/driver\/"/);
    expect(html).toContain('Tôi cần đi xe');
    expect(html).toContain('Tôi lái xe');
  });

  it.each([
    ['?role=driver', '/driver/'],
    ['?role=rider', '/rider/'],
  ])('sends the legacy link %s to %s', (search, target) => {
    expect(legacyRoleRedirect(search)).toBe(target);
  });

  it.each(['', '?role=admin', '?other=1'])('keeps the chooser for %j', search => {
    expect(legacyRoleRedirect(search)).toBeNull();
  });
});

describe('avatar initials', () => {
  it.each([
    ['demo-rider', 'DR'],
    ['demo-driver', 'DD'],
    ['alice', 'AL'],
    ['', '?'],
  ])('derives initials for %j', (userId, expected) => {
    expect(initialsOf(userId)).toBe(expected);
  });
});

describe('trip history list', () => {
  const base = {
    pickupName: 'Chợ Bến Thành',
    destinationName: 'Landmark 81',
    createdAt: '2026-10-17T10:00:00Z',
    fareAmount: '50000',
  };

  it('shows an empty state with the provided copy', () => {
    const html = renderToStaticMarkup(<TripHistoryList items={[]} emptyTitle="Chưa có chuyến" emptyHint="Hãy đi thử" />);
    expect(html).toContain('Chưa có chuyến');
    expect(html).toContain('Hãy đi thử');
  });

  it('labels paid, cancelled and failed trips differently', () => {
    const items = [
      { ...base, id: 'a', status: 'Paid' },
      { ...base, id: 'b', status: 'Cancelled' },
      { ...base, id: 'c', status: 'Failed' },
    ] as TripResponse[];
    const html = renderToStaticMarkup(<TripHistoryList items={items} emptyTitle="" emptyHint="" />);
    expect(html).toContain('Đã thanh toán');
    expect(html).toContain('Đã hủy chuyến');
    expect(html).toContain('Không thành công');
    expect(html).toContain('50.000 ₫');
  });
});
