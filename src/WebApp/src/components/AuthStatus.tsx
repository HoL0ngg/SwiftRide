import { useEffect, useState } from 'react';
import { LogIn, LogOut, ShieldAlert, ShieldCheck } from 'lucide-react';
import type { AuthSession } from '../services/auth';

interface AuthStatusProps {
  session: AuthSession;
  onLoginRequest?: () => void;
  /** Use on dark bars (driver topbar). */
  inverted?: boolean;
}

export default function AuthStatus({ session, onLoginRequest, inverted = false }: AuthStatusProps) {
  const [isAuthenticated, setIsAuthenticated] = useState<boolean>(() => session.isAuthenticated());
  const [actor, setActor] = useState(() => session.getActor());
  const [error, setError] = useState<string | null>(null);
  const [isProcessing, setIsProcessing] = useState(false);

  const mode = session.getAuthMode();
  const tone = inverted ? 'pill' : 'pill pill-info';
  const toneStyle = inverted ? { background: 'rgb(255 255 255 / 0.14)', color: '#fff' } : undefined;

  useEffect(() => {
    const unsubscribe = session.onAuthStateChanged(isAuth => {
      setIsAuthenticated(isAuth);
      setActor(session.getActor());
      if (isAuth) {
        setError(null);
      }
    });

    return () => {
      unsubscribe();
    };
  }, [session]);

  async function handleSignIn() {
    setIsProcessing(true);
    setError(null);
    try {
      if (onLoginRequest) {
        onLoginRequest();
      } else {
        await session.signIn();
      }
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Đăng nhập thất bại.');
    } finally {
      setIsProcessing(false);
    }
  }

  async function handleSignOut() {
    setIsProcessing(true);
    setError(null);
    try {
      await session.signOut();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : 'Đăng xuất thất bại.');
    } finally {
      setIsProcessing(false);
    }
  }

  return (
    <div className="hide-on-mobile" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
      {mode === 'oidc' ? (
        isAuthenticated ? (
          <span className={tone} style={toneStyle}>
            <ShieldCheck size={14} aria-hidden="true" />
            <span>
              <strong>{actor.userId}</strong> ({actor.role})
            </span>
            <button
              type="button"
              onClick={handleSignOut}
              disabled={isProcessing}
              className="chip-btn"
              style={{ minHeight: 28, padding: '0 10px', fontSize: 12 }}
              title="Đăng xuất khỏi OIDC IdP"
            >
              <LogOut size={12} aria-hidden="true" />
              Đăng xuất
            </button>
          </span>
        ) : (
          <button type="button" onClick={handleSignIn} disabled={isProcessing} className="btn btn-primary btn-sm">
            <LogIn size={15} aria-hidden="true" />
            {isProcessing ? 'Đang chuyển hướng...' : 'Đăng nhập OIDC'}
          </button>
        )
      ) : (
        <span className={tone} style={toneStyle} title="Chế độ phát triển sử dụng JWT dev-token tự sinh">
          <span
            aria-hidden="true"
            style={{ width: 8, height: 8, borderRadius: '50%', background: '#22c55e', display: 'inline-block' }}
          />
          Dev token · {actor.role}
        </span>
      )}

      {error && (
        <span className="pill pill-danger" title={error}>
          <ShieldAlert size={14} aria-hidden="true" />
          <span style={{ maxWidth: 160, overflow: 'hidden', textOverflow: 'ellipsis' }}>{error}</span>
        </span>
      )}
    </div>
  );
}
