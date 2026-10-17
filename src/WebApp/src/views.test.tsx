import { renderToStaticMarkup } from 'react-dom/server'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { DemoState, Place, Trip, TripStatus } from './demo'
import RiderView from './RiderView'
import DriverView from './DriverView'

const mock = vi.hoisted(() => ({
  state: { trip: null, history: [], online: true } as DemoState,
  places: [
    { id: 'pickup', name: 'Chợ Bến Thành', address: 'Quận 1', lat: 10.7725, lng: 106.698 },
    { id: 'destination', name: 'Landmark 81', address: 'Bình Thạnh', lat: 10.7951, lng: 106.7219 },
  ] as Place[],
}))

vi.mock('./demo', () => ({
  places: mock.places,
  useDemo: () => mock.state,
  formatMoney: (value: number) => `${value} VND`,
  demoApi: {
    quote: vi.fn(), confirmQuote: vi.fn(), cancel: vi.fn(), settlePayment: vi.fn(), reset: vi.fn(),
    driverAccept: vi.fn(), driverReject: vi.fn(), enRoute: vi.fn(), pickup: vi.fn(), dropoff: vi.fn(), setOnline: vi.fn(),
  },
}))

function trip(status: TripStatus, fare = 50000): Trip {
  return {
    id: `trip-${status}`, pickup: mock.places[0], destination: mock.places[1],
    status, fare, distanceKm: 4.6, durationMinutes: 18, rejections: 0,
    createdAt: '2026-10-17T10:00:00Z', paymentMethod: 'wallet',
  }
}

const text = (html: string) => html.replace(/<[^>]+>/g, ' ').replace(/\s+/g, ' ').trim()
const buttons = (html: string) => Array.from(html.matchAll(/<button\b[^>]*>([\s\S]*?)<\/button>/g), match => text(match[1]))

beforeEach(() => {
  mock.state = { trip: null, history: [], online: true }
})

describe('rider booking and trip controls', () => {
  it('starts with two labeled places and requests a quote before confirmation', () => {
    const html = renderToStaticMarkup(<RiderView />)
    expect(html).toMatch(/<select[^>]+aria-label="Điểm đón"/)
    expect(html).toMatch(/<select[^>]+aria-label="Điểm đến"/)
    expect(buttons(html)).toContain('Xem giá chuyến đi')
    expect(buttons(html)).not.toContain('Xác nhận giá &amp; đặt xe')
    expect(text(html)).toContain('Ví SwiftPay')
    expect(text(html)).toContain('Thẻ demo')
  })

  it('offers price confirmation and rejection on a quote', () => {
    mock.state.trip = trip('Quoted')
    const actions = buttons(renderToStaticMarkup(<RiderView />))
    expect(actions).toContain('Xác nhận giá &amp; đặt xe')
    expect(actions).toContain('Chọn lại hành trình')
  })

  it('allows cancellation while waiting for driver acceptance', () => {
    mock.state.trip = trip('DriverOffered')
    expect(buttons(renderToStaticMarkup(<RiderView />))).toContain('Hủy chuyến đi')
  })

  it.each<TripStatus>(['DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending', 'Paid'])(
    'does not offer cancellation once the trip is %s', status => {
      mock.state.trip = trip(status)
      const actions = buttons(renderToStaticMarkup(<RiderView />))
      expect(actions).not.toContain('Hủy chuyến đi')
      expect(actions).not.toContain('Chọn lại hành trình')
    },
  )
})

describe('payment state is visible to both roles', () => {
  it.each([['rider', RiderView], ['driver', DriverView]] as const)(
    '%s keeps PaymentPending distinct from a paid trip', (_role, View) => {
      mock.state.trip = trip('PaymentPending')
      const html = renderToStaticMarkup(<View />)
      expect(text(html)).toContain('Đang xử lý thanh toán')
      expect(text(html)).not.toContain('Đã thanh toán')
      expect(buttons(html)).toContain('Mô phỏng thanh toán thành công')
    },
  )
})

describe('driver actions and completed trip totals', () => {
  it('shows acceptance and rejection only while a driver offer is waiting', () => {
    mock.state.trip = trip('DriverOffered')
    const actions = buttons(renderToStaticMarkup(<DriverView />))
    expect(actions).toContain('Nhận chuyến')
    expect(actions).toContain('Từ chối')
    expect(actions).not.toContain('Bắt đầu đến đón')
    expect(actions).not.toContain('Đã đón khách')
    expect(actions).not.toContain('Đã trả khách')
  })

  it.each([
    ['DriverAccepted', 'Bắt đầu đến đón'],
    ['EnRoute', 'Đã đón khách'],
    ['PickedUp', 'Đã trả khách'],
  ] as const)('shows only the next lifecycle action for %s', (status, expected) => {
    mock.state.trip = trip(status)
    const actions = buttons(renderToStaticMarkup(<DriverView />))
    const lifecycle = ['Bắt đầu đến đón', 'Đã đón khách', 'Đã trả khách', 'Nhận chuyến', 'Từ chối']
    expect(actions.filter(action => lifecycle.includes(action))).toEqual([expected])
  })

  it('counts and sums only paid trips in the driver statistics', () => {
    mock.state.history = [trip('Paid', 50000), { ...trip('Paid', 75000), id: 'paid-2' }, trip('Cancelled', 800000), trip('Failed', 900000)]
    const content = text(renderToStaticMarkup(<DriverView />))
    expect(content).toMatch(/Chuyến hoàn tất\s+2\s+chuyến/)
    expect(content).toContain('125000 VND')
    expect(content).not.toContain('1825000 VND')
  })
})
