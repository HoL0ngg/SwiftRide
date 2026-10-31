export type Place = {
  id: string;
  name: string;
  address: string;
  lat: number;
  lng: number;
};

export function isPlace(value: unknown): value is Place {
  if (!value || typeof value !== 'object') return false;
  const place = value as Partial<Place>;
  return (
    typeof place.id === 'string' &&
    place.id.trim().length > 0 &&
    place.id.length <= 200 &&
    typeof place.name === 'string' &&
    place.name.trim().length > 0 &&
    place.name.length <= 1000 &&
    typeof place.address === 'string' &&
    place.address.trim().length > 0 &&
    place.address.length <= 2000 &&
    typeof place.lat === 'number' &&
    Number.isFinite(place.lat) &&
    Math.abs(place.lat) <= 90 &&
    typeof place.lng === 'number' &&
    Number.isFinite(place.lng) &&
    Math.abs(place.lng) <= 180
  );
}
