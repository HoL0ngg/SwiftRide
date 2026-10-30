import { useEffect, useState } from 'react';
import { LogIn, LogOut, ShieldAlert, ShieldCheck } from 'lucide-react';
import type { AuthSession } from '../services/auth';

interface AuthStatusProps {
  session: AuthSession;
  onLoginRequest?: () => void;
}

export default function AuthStatus({ session, onLoginRequest }: AuthStatusProps) {
  const [isAuthenticated, setIsAuthenticated] = useState<boolean>(() => session.isAuthenticated());
  const [actor, setActor] = useState(() => session.getActor());
  const [error, setError] = useState<string | null>(null);
  const [isProcessing, setIsProcessing] = useState(false);

  const mode = session.getAuthMode();

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
    <div className="flex items-center gap-2 text-xs">
      {mode === 'oidc' ? (
        <div className="flex items-center gap-2">
          {isAuthenticated ? (
            <div className="flex items-center gap-2 rounded-full border border-emerald-300 bg-emerald-50 px-2.5 py-1 text-emerald-800">
              <ShieldCheck size={14} className="text-emerald-600" />
              <span>
                <strong>{actor.userId}</strong> ({actor.role})
              </span>
              <button
                type="button"
                onClick={handleSignOut}
                disabled={isProcessing}
                className="ml-1 inline-flex items-center gap-1 rounded bg-white px-1.5 py-0.5 text-[10px] font-semibold text-rose-700 shadow-xs hover:bg-rose-50"
                title="Đăng xuất khỏi OIDC IdP"
              >
                <LogOut size={11} />
                Đăng xuất
              </button>
            </div>
          ) : (
            <button
              type="button"
              onClick={handleSignIn}
              disabled={isProcessing}
              className="inline-flex items-center gap-1.5 rounded-full border border-emerald-600 bg-emerald-600 px-3 py-1 font-medium text-white shadow-xs hover:bg-emerald-700 disabled:opacity-50"
            >
              <LogIn size={13} />
              {isProcessing ? 'Đang chuyển hướng...' : 'Đăng nhập OIDC'}
            </button>
          )}
        </div>
      ) : (
        <span
          className="inline-flex items-center gap-1 rounded-full border border-stone-200 bg-stone-100 px-2.5 py-1 font-mono text-[10px] text-stone-600"
          title="Chế độ phát triển sử dụng JWT dev-token tự sinh"
        >
          <span className="h-1.5 w-1.5 rounded-full bg-emerald-500" />
          Dev Token ({actor.role})
        </span>
      )}

      {error && (
        <div className="inline-flex items-center gap-1 text-[11px] text-rose-600" title={error}>
          <ShieldAlert size={13} />
          <span className="max-w-[150px] truncate">{error}</span>
        </div>
      )}
    </div>
  );
}
