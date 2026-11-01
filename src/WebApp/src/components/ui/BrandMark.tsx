import { Zap } from 'lucide-react';

type Props = {
  /** Optional role label shown next to the wordmark, e.g. "Tài xế". */
  roleLabel?: string;
  href?: string;
};

export default function BrandMark({ roleLabel, href = '/' }: Props) {
  return (
    <a className="brand" href={href} aria-label="SwiftRide trang chủ">
      <span className="brand-mark">
        <Zap size={20} fill="currentColor" aria-hidden="true" />
      </span>
      <span>swiftride</span>
      {roleLabel && <span className="brand-role">{roleLabel}</span>}
    </a>
  );
}
