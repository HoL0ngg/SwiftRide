import type { ReactNode } from 'react';

export type PillTone = 'info' | 'success' | 'warning' | 'danger' | 'neutral';

type Props = {
  tone?: PillTone;
  children: ReactNode;
};

export default function StatusPill({ tone = 'info', children }: Props) {
  return <span className={`pill pill-${tone}`}>{children}</span>;
}
