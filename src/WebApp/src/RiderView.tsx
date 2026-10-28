import { useRef, useState } from 'react'
import {
  ArrowRight, ArrowUpDown, CarFront, Check, CheckCircle2,
  Clock3, CreditCard, Leaf, LoaderCircle, MapPin, Navigation,
  ShieldCheck, Sparkles, Star, Wallet,
} from 'lucide-react'
import { useDemo, demoApi, formatMoney } from './demo'
import type { Place } from './demo'
import AddressSearch from './AddressSearch'

const statusCopy: Record<string, { title: string; description: string }> = {
  Quoted: { title: 'Chuyến đi của bạn đã sẵn sàng', description: 'Xem giá và xác nhận để gửi lời mời đến tài xế.' },
  DriverOffered: { title: 'Đang kết nối tài xế', description: 'Lời mời đã được gửi. Chuyển sang giao diện tài xế để nhận chuyến demo.' },
  DriverAccepted: { title: 'Đã có tài xế đồng hành', description: 'Tài xế đã nhận chuyến và đang chuẩn bị đến đón bạn.' },
  EnRoute: { title: 'Tài xế đang đến đón', description: 'Hãy sẵn sàng tại điểm đón. Một hành trình mới sắp bắt đầu.' },
  PickedUp: { title: 'Chúc bạn một chuyến đi vui', description: 'Bạn đang trên đường đến điểm đến. Cứ thư giãn nhé.' },
  DroppedOff: { title: 'Bạn đã đến nơi', description: 'Chuyến đi đã kết thúc. Hoàn tất bước thanh toán mô phỏng bên dưới.' },
  PaymentPending: { title: 'Đang xử lý thanh toán', description: 'Chuyến đi chưa được thanh toán. Bạn có thể hoàn tất bước mô phỏng bên dưới.' },
  Paid: { title: 'Cảm ơn bạn đã chọn SwiftRide', description: 'Thanh toán thành công. Hẹn gặp bạn trong hành trình tiếp theo!' },
  Cancelled: { title: 'Chuyến đi đã được hủy', description: 'Khi cần di chuyển, SwiftRide luôn sẵn sàng cùng bạn.' },
  Failed: { title: 'Chưa tìm được tài xế phù hợp', description: 'Hiện chưa thể kết nối chuyến đi này. Bạn có thể thử lại sau.' },
}

const journeySteps = [
  { label: 'Kết nối', states: ['DriverOffered'] },
  { label: 'Đến đón', states: ['DriverAccepted', 'EnRoute'] },
  { label: 'Di chuyển', states: ['PickedUp'] },
  { label: 'Hoàn tất', states: ['DroppedOff', 'PaymentPending', 'Paid'] },
]

type Props = {
  pickup: Place; destination: Place; selection: 'pickup' | 'destination' | null;
  onChange: (kind: 'pickup' | 'destination', place: Place) => void;
  onSelectMap: (kind: 'pickup' | 'destination') => void;
  onSwap: () => void;
};

export default function RiderView({ pickup, destination, selection, onChange, onSelectMap, onSwap }: Props) {
  const { trip, online } = useDemo()
  const [token, setToken] = useState('')
  const [paymentMethod, setPaymentMethod] = useState<'wallet' | 'card'>('wallet')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const submitting = useRef(false)

  async function run(action: () => Promise<unknown>) {
    if (submitting.current) return
    submitting.current = true
    setLoading(true)
    setError('')
    try {
      await action()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Chưa thể thực hiện thao tác. Vui lòng thử lại.')
    } finally {
      submitting.current = false
      setLoading(false)
    }
  }

  const terminal = trip && ['Paid', 'Cancelled', 'Failed'].includes(trip.status)
  const quoted = trip?.status === 'Quoted'
  const copy = trip ? statusCopy[trip.status] : undefined
  const currentStep = trip ? journeySteps.findIndex(step => step.states.includes(trip.status)) : -1
  const canCancel = trip && ['Quoted', 'DriverOffered'].includes(trip.status)

  return (
    <section aria-label="Đặt chuyến cho hành khách" className="rider-panel">
      <div className="mb-7 flex items-center justify-between gap-3">
        <div className="eyebrow flex items-center gap-2"><span className="h-1.5 w-1.5 rounded-full bg-emerald-600" /> HÀNH TRÌNH CỦA BẠN</div>
        <span className="rounded-full border border-stone-200 bg-white px-2.5 py-1 text-[10px] font-semibold tracking-wide text-stone-500">RIDER</span>
      </div>

      {!trip ? (
        <>
          <h2 className="panel-title">Đi đâu cũng<br /><span className="text-emerald-700">thật gần.</span></h2>
          <p className="muted mt-3 max-w-72 text-sm leading-6">Một điểm đến mới, một hành trình êm ái.<br className="hidden sm:block" /> Bắt đầu cùng SwiftRide nhé.</p>

          <form className="mt-7" onSubmit={event => { event.preventDefault(); void run(() => demoApi.quote(pickup, destination, paymentMethod)) }}>
            <div className="space-y-4 rounded-2xl border border-stone-200 bg-[#fafbf9] p-3">
              <AddressSearch label="Điểm đón" place={pickup} token={token} disabled={loading} selecting={selection === 'pickup'} onChange={place => onChange('pickup', place)} onSelectMap={() => onSelectMap('pickup')} />
              <button type="button" aria-label="Đổi điểm đón và điểm đến" disabled={loading} onClick={onSwap} className="flex items-center gap-2 text-[10px] text-stone-500 hover:text-emerald-700"><ArrowUpDown size={13} aria-hidden="true" />Đổi hai điểm</button>
              <AddressSearch label="Điểm đến" place={destination} token={token} disabled={loading} selecting={selection === 'destination'} onChange={place => onChange('destination', place)} onSelectMap={() => onSelectMap('destination')} />
            </div>
            <p className="mt-3 text-[10px] leading-5 text-stone-500">Tìm kiếm bằng Nominatim · © <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener noreferrer" className="underline">OpenStreetMap contributors</a>. Hai điểm ban đầu là địa điểm mẫu; kiểm tra trên bản đồ trước khi xem giá.</p>
            <details className="mt-2 text-[10px] text-stone-500" open={!import.meta.env.DEV}>
              <summary className="cursor-pointer">{import.meta.env.DEV ? 'Kết nối Gateway (JWT Development tự cấp)' : 'JWT để tìm địa chỉ'}</summary>
              <label className="mt-2 block">JWT từ hệ thống đăng nhập<input type="password" autoComplete="off" value={token} onChange={event => setToken(event.target.value)} placeholder="Chỉ lưu trong bộ nhớ tab" className="mt-1 w-full rounded-lg border border-stone-200 p-2" /></label>
            </details>
            <div className="section-divider my-6" />
            <div className="mb-3 flex items-center justify-between"><h2 className="text-xs font-semibold text-stone-700">Thanh toán</h2><span className="text-[10px] text-stone-400">Phương thức mô phỏng</span></div>
            <div className="grid grid-cols-2 gap-2" role="group" aria-label="Phương thức thanh toán">
              {([{ value: 'wallet', label: 'Ví SwiftPay', Icon: Wallet }, { value: 'card', label: 'Thẻ demo', Icon: CreditCard }] as const).map(({ value, label, Icon }) => <button key={value} type="button" disabled={loading} aria-pressed={paymentMethod === value} onClick={() => setPaymentMethod(value)} className={`flex min-h-12 items-center gap-2 rounded-xl border px-3 text-xs font-medium transition ${paymentMethod === value ? 'border-emerald-600 bg-emerald-50 text-emerald-800' : 'border-stone-200 text-stone-500 hover:border-emerald-300'}`}><Icon size={17} aria-hidden="true" /><span className="flex-1 text-left">{label}</span>{paymentMethod === value && <Check size={14} aria-hidden="true" />}</button>)}
            </div>

            {!online && <p className="mt-4 rounded-xl bg-amber-50 p-3 text-xs leading-5 text-amber-800">Tài xế đang ngoại tuyến. Chuyển sang giao diện tài xế và bật nhận chuyến để trải nghiệm đặt xe.</p>}
            {error && <p role="alert" className="error-message mt-4">{error}</p>}
            <button type="submit" disabled={loading || (pickup.lat === destination.lat && pickup.lng === destination.lng)} className="primary-button mt-5 flex w-full items-center justify-center gap-2">{loading ? <LoaderCircle size={17} className="animate-spin" aria-hidden="true" /> : <Navigation size={17} aria-hidden="true" />}<span>{loading ? 'Đang tìm chuyến phù hợp…' : 'Xem giá chuyến đi'}</span>{!loading && <ArrowRight size={17} className="ml-auto" aria-hidden="true" />}</button>
            {pickup.lat === destination.lat && pickup.lng === destination.lng && <p className="mt-2 text-xs text-amber-800">Vui lòng chọn điểm đến khác điểm đón.</p>}
          </form>

          <p className="mt-3 text-[10px] leading-5 text-stone-500">Giá và thời gian là mô phỏng từ khoảng cách Haversine; chưa phải tuyến đường hoặc báo giá backend.</p><div className="mt-6 flex items-start gap-3 rounded-2xl border border-emerald-100 bg-emerald-50/60 p-4"><span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-white text-emerald-700"><Leaf size={18} aria-hidden="true" /></span><div><p className="text-xs font-semibold text-emerald-900">Đi nhẹ nhàng. Đến an tâm.</p><p className="mt-1 text-[11px] leading-5 text-emerald-800/70">Giá rõ ràng trước khi đặt.<br />Điểm đón và điểm đến rõ ràng trên bản đồ.</p></div></div>
        </>
      ) : (
        <>
          <span className={`status-pill mb-4 inline-flex items-center gap-1.5 ${trip.status === 'Paid' ? 'bg-emerald-100 text-emerald-800' : ''}`}>{trip.status === 'Paid' ? <CheckCircle2 size={13} aria-hidden="true" /> : <span className="h-1.5 w-1.5 rounded-full bg-current" />}{quoted ? 'GIÁ DEMO ƯỚC TÍNH' : terminal ? 'CHUYẾN ĐI ĐÃ KẾT THÚC' : 'CHUYẾN ĐI HIỆN TẠI'}</span>
          <h2 className="text-[27px] font-semibold leading-[1.35] tracking-[-.04em] text-stone-900">{copy?.title}</h2>
          <p aria-live="polite" className="muted mt-3 text-xs leading-6">{copy?.description}</p>

          {!quoted && !terminal && <ol aria-label="Tiến trình chuyến đi" className="my-6 flex">{journeySteps.map((step, index) => <li key={step.label} aria-current={index === currentStep ? 'step' : undefined} className="relative flex flex-1 flex-col items-center gap-2">{index < journeySteps.length - 1 && <span className={`absolute left-1/2 top-3 h-px w-full ${index < currentStep ? 'bg-emerald-600' : 'bg-stone-200'}`} />}<span className={`relative flex h-6 w-6 items-center justify-center rounded-full text-[10px] font-semibold ${index <= currentStep ? 'bg-emerald-700 text-white' : 'border border-stone-200 bg-white text-stone-400'}`}>{index < currentStep ? <Check size={12} aria-hidden="true" /> : index + 1}</span><span className={`text-[10px] ${index === currentStep ? 'font-semibold text-emerald-800' : 'text-stone-400'}`}>{step.label}</span></li>)}</ol>}

          {!quoted && !terminal && trip.status !== 'DriverOffered' && <div className="mt-5 flex items-center gap-3 rounded-2xl border border-stone-200 p-4"><div className="relative flex h-12 w-12 shrink-0 items-center justify-center rounded-full bg-[#e8eae0] text-sm font-semibold text-emerald-900">HT<span className="absolute -bottom-1 -right-1 flex h-5 w-5 items-center justify-center rounded-full border-2 border-white bg-emerald-700 text-white"><Check size={10} aria-hidden="true" /></span></div><div className="min-w-0 flex-1"><div className="flex items-center justify-between gap-2"><p className="text-xs font-semibold text-stone-800">Hoàng Tuấn</p><span className="flex items-center gap-1 text-[10px] font-medium"><Star size={11} className="fill-amber-400 text-amber-400" aria-hidden="true" />4.9</span></div><p className="mt-1 text-[10px] text-stone-500">Toyota Vios · Trắng</p><p className="mt-1 text-[10px] font-semibold tracking-wider text-stone-700">59A-123.45</p></div></div>}

          <div className="mt-6 rounded-2xl border border-stone-200 bg-[#fafbf9] p-4">
            <div className="relative space-y-5"><span className="absolute left-[6px] top-4 h-10 border-l border-dashed border-stone-300" /><div className="relative flex gap-3"><span className="mt-1 h-3 w-3 shrink-0 rounded-full border-[3px] border-emerald-600 bg-white" /><div><p className="text-xs font-semibold text-stone-800">{trip.pickup.name}</p><p className="mt-1 text-[10px] leading-4 text-stone-500">{trip.pickup.address}</p></div></div><div className="relative flex gap-3"><MapPin size={14} className="mt-0.5 -ml-px shrink-0 text-emerald-700" aria-hidden="true" /><div><p className="text-xs font-semibold text-stone-800">{trip.destination.name}</p><p className="mt-1 text-[10px] leading-4 text-stone-500">{trip.destination.address}</p></div></div></div>
            <div className="mt-4 flex items-center gap-4 border-t border-stone-200 pt-3 text-[10px] text-stone-500"><span className="flex items-center gap-1.5"><Navigation size={12} aria-hidden="true" />{trip.distanceKm.toLocaleString('vi-VN')} km</span><span className="flex items-center gap-1.5"><Clock3 size={12} aria-hidden="true" />Khoảng {trip.durationMinutes} phút</span></div>
          </div>

          <div className="mt-5 flex items-center gap-3 rounded-2xl border border-emerald-200 bg-emerald-50/60 p-4"><div className="flex h-12 w-14 items-center justify-center rounded-xl bg-white text-emerald-800"><CarFront size={30} strokeWidth={1.3} aria-hidden="true" /></div><div className="flex-1"><p className="text-sm font-semibold text-stone-800">SwiftCar</p><p className="mt-1 text-[10px] text-stone-500">4 chỗ · Thoải mái mỗi ngày</p></div><div className="text-right"><p className="text-lg font-bold tracking-tight text-emerald-900">{formatMoney(trip.fare)}</p><p className="mt-1 text-[9px] text-emerald-700">{quoted ? 'Giá trước khi xác nhận' : 'Giá đã xác nhận'}</p></div></div>
          <div className="mt-3 flex items-center justify-between px-1 text-[11px] text-stone-500"><span className="flex items-center gap-2">{trip.paymentMethod === 'wallet' ? <Wallet size={14} aria-hidden="true" /> : <CreditCard size={14} aria-hidden="true" />}{trip.paymentMethod === 'wallet' ? 'Ví SwiftPay' : 'Thẻ demo'}</span><span>{trip.status === 'Paid' ? 'Đã thanh toán' : 'Thanh toán mô phỏng'}</span></div>

          {error && <p role="alert" className="error-message mt-4">{error}</p>}
          <div className="mt-6 space-y-3">
            {quoted && <button disabled={loading} onClick={() => void run(() => demoApi.confirmQuote())} className="primary-button flex w-full items-center justify-center gap-2">{loading ? <LoaderCircle size={17} className="animate-spin" aria-hidden="true" /> : <Check size={17} aria-hidden="true" />}Xác nhận giá & đặt xe<ArrowRight size={17} className="ml-auto" aria-hidden="true" /></button>}
            {(trip.status === 'DroppedOff' || trip.status === 'PaymentPending') && <button disabled={loading} onClick={() => void run(() => demoApi.settlePayment())} className="primary-button flex w-full items-center justify-center gap-2">{loading ? <LoaderCircle size={17} className="animate-spin" aria-hidden="true" /> : <Wallet size={17} aria-hidden="true" />}Mô phỏng thanh toán thành công</button>}
            {terminal && <button disabled={loading} onClick={() => void run(() => demoApi.reset())} className="primary-button flex w-full items-center justify-center gap-2"><Sparkles size={16} aria-hidden="true" />Đặt một chuyến mới<ArrowRight size={17} className="ml-auto" aria-hidden="true" /></button>}
            {canCancel && <button disabled={loading} onClick={() => void run(() => demoApi.cancel())} className="secondary-button w-full">{quoted ? 'Chọn lại hành trình' : 'Hủy chuyến đi'}</button>}
          </div>
          <div className="mt-6 flex items-start justify-center gap-2 text-[10px] leading-5 text-stone-400"><ShieldCheck size={14} className="mt-0.5 shrink-0 text-emerald-700" aria-hidden="true" /><p>{quoted ? 'Giá được giữ nguyên sau khi bạn xác nhận.' : 'Thông tin chuyến đi được đồng bộ giữa hai giao diện demo.'}</p></div>
        </>
      )}
    </section>
  )
}
