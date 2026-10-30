import { isPlace, type Place } from '../types/place';
import { type AuthSession, createAuthSession } from './auth';

async function request(path: string, options: RequestInit = {}): Promise<unknown> {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 12_000);
  try {
    const response = await fetch(path, { ...options, signal: controller.signal });
    if (!response.ok) {
      const hint =
        response.status === 401 || response.status === 403
          ? 'Kiểm tra JWT hoặc quyền truy cập.'
          : response.status === 429
            ? 'Đang có nhiều lượt tìm kiếm. Hãy chờ rồi nhấn Tìm lại.'
            : 'Kiểm tra Gateway và thử lại sau.';
      throw new Error(`Không tìm được địa chỉ (HTTP ${response.status}). ${hint}`);
    }
    return await response.json();
  } catch (reason) {
    if (controller.signal.aborted) throw new Error('Tìm địa chỉ quá thời gian. Hãy nhấn Tìm để thử lại.');
    if (reason instanceof TypeError) throw new Error('Không kết nối được Gateway. Kiểm tra kết nối và thử lại.');
    throw reason;
  } finally {
    clearTimeout(timeout);
  }
}

export async function searchLocations(query: string, auth: string | AuthSession): Promise<Place[]> {
  const normalized = query.trim();
  if (normalized.length < 3 || normalized.length > 200) throw new Error('Nhập địa chỉ từ 3 đến 200 ký tự.');
  let accessToken = '';
  if (typeof auth === 'string') {
    accessToken = auth.trim();
    if (!accessToken) {
      const devSession = createAuthSession({ userId: 'demo-rider', role: 'rider' });
      accessToken = await devSession.getToken();
    }
  } else if (auth && typeof auth.getToken === 'function') {
    accessToken = await auth.getToken();
  }
  if (!accessToken) throw new Error('Nhập JWT để tìm địa chỉ qua Gateway.');
  const response = await request(`/locations/search?query=${encodeURIComponent(normalized)}`, {
    headers: { Authorization: `Bearer ${accessToken}` },
  });
  if (!Array.isArray(response)) throw new Error('Dữ liệu địa chỉ không hợp lệ.');
  return response.map((item: unknown) => {
    if (!item || typeof item !== 'object') throw new Error('Dữ liệu địa chỉ không hợp lệ.');
    const location = item as Record<string, unknown>;
    const place = {
      id: location.id,
      name: typeof location.displayName === 'string' ? location.displayName.split(',')[0].trim() : '',
      address: location.displayName,
      lat: location.latitude,
      lng: location.longitude,
    };
    if (!isPlace(place)) throw new Error('Dữ liệu địa chỉ không hợp lệ.');
    return place;
  });
}
