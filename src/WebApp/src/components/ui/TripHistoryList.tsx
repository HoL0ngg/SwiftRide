import { Check, History, X } from 'lucide-react';
import type { TripResponse } from '../../types/tripTypes';
import { formatVnd } from '../../utils/money';
import StatusPill from './StatusPill';

type Props = {
  items: TripResponse[];
  emptyTitle: string;
  emptyHint: string;
};

function statusLabel(status: TripResponse['status']): { text: string; tone: 'success' | 'warning' } {
  if (status === 'Paid') return { text: 'Đã thanh toán', tone: 'success' };
  if (status === 'Cancelled') return { text: 'Đã hủy chuyến', tone: 'warning' };
  return { text: 'Không thành công', tone: 'warning' };
}

export default function TripHistoryList({ items, emptyTitle, emptyHint }: Props) {
  if (items.length === 0) {
    return (
      <div className="empty">
        <History size={44} aria-hidden="true" />
        <h3 className="panel-title" style={{ fontSize: 20 }}>{emptyTitle}</h3>
        <p className="muted" style={{ maxWidth: 360 }}>{emptyHint}</p>
      </div>
    );
  }

  return (
    <ul className="history-list" aria-label="Lịch sử chuyến đi">
      {items.map(item => {
        const status = statusLabel(item.status);
        const paid = item.status === 'Paid';
        return (
          <li key={item.id} className="history-item">
            <span className={`history-icon ${paid ? 'history-icon--ok' : 'history-icon--fail'}`}>
              {paid ? <Check size={22} aria-hidden="true" /> : <X size={22} aria-hidden="true" />}
            </span>
            <div style={{ minWidth: 0, flex: 1 }}>
              <h3 style={{ fontSize: 15, fontWeight: 600, lineHeight: 1.4 }}>
                {item.pickupName || 'Điểm đón'} → {item.destinationName || 'Điểm đến'}
              </h3>
              <p className="caption" style={{ marginTop: 2 }}>
                {new Date(item.createdAt).toLocaleString('vi-VN')}
              </p>
              <div style={{ marginTop: 8 }}>
                <StatusPill tone={status.tone}>{status.text}</StatusPill>
              </div>
            </div>
            <strong style={{ whiteSpace: 'nowrap' }}>{formatVnd(item.fareAmount)}</strong>
          </li>
        );
      })}
    </ul>
  );
}
