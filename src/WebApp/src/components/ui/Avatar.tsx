type Props = {
  /** Any stable identifier; only its initials are rendered. */
  userId: string;
};

export function initialsOf(userId: string): string {
  const parts = userId.split(/[\s\-_.@]+/).filter(Boolean);
  const letters = (parts.length > 1 ? parts.slice(0, 2).map(part => part[0]) : [userId.slice(0, 2)])
    .join('')
    .toUpperCase();
  return letters || '?';
}

export default function Avatar({ userId }: Props) {
  return (
    <span className="avatar" aria-hidden="true">
      {initialsOf(userId)}
    </span>
  );
}
