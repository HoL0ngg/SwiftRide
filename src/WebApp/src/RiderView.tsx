import { useRef, useState } from 'react'
import {
  ArrowRight, ArrowUpDown, CarFront, Check, CheckCircle2, ChevronDown,
  Clock3, CreditCard, Leaf, LoaderCircle, MapPin, Navigation,
  ShieldCheck, Sparkles, Star, Wallet,
} from 'lucide-react'
import { places, useDemo, demoApi, formatMoney } from './demo'

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

export default function RiderView() {
  const { trip, online } = useDemo()
  const [pickupId, setPickupId] = useState(places[0]?.id ?? '')
  const [destinationId, setDestinationId] = useState(places[1]?.id ?? '')
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

          <form className="mt-7" onSubmit={event => { event.preventDefault(); void run(() => demoApi.quote(pickupId, destinationId, paymentMethod)) }}>
            <div className="relative rounded-2xl border border-stone-200 bg-[#fafbf9] p-1.5">
              <div className="pointer-events-none absolute left-[25px] top-[49px] h-10 border-l border-dashed border-stone-300" />
              <label className="relative flex items-center gap-3 rounded-xl px-3 py-3 focus-within:bg-white focus-within:ring-2 focus-within:ring-emerald-600">
                <span className="flex h-3 w-3 shrink-0 items-center justify-center rounded-full border-2 border-emerald-600"><span className="h-1 w-1 rounded-full bg-emerald-600" /></span>
                <span className="min-w-0 flex-1">
                  <span className="block text-[10px] font-semibold uppercase tracking-[.12em] text-stone-400">Điểm đón</span>
                  <select aria-label="Điểm đón" value={pickupId} onChange={event => setPickupId(event.target.value)} disabled={loading} className="w-full cursor-pointer appearance-none bg-transparent py-1 pr-3 text-sm font-semibold text-stone-800 outline-none">
                    {places.map(place => <option key={place.id} value={place.id}>{place.name}</option>)}
                  </select>
                </span>
                <ChevronDown size={15} className="shrink-0 text-stone-400" aria-hidden="true" />
              </label>
              <div className="ml-10 mr-12 border-t border-stone-200" />
              <button type="button" aria-label="Đổi điểm đón và điểm đến" disabled={loading} onClick={() => { setPickupId(destinationId); setDestinationId(pickupId) }} className="absolute right-5 top-[60px] z-10 flex h-7 w-7 items-center justify-center rounded-full border border-stone-200 bg-white text-stone-500 transition hover:border-emerald-600 hover:text-emerald-700"><ArrowUpDown size={13} aria-hidden="true" /></button>
              <label className="relative flex items-center gap-3 rounded-xl px-3 py-3 focus-within:bg-white focus-within:ring-2 focus-within:ring-emerald-600">
                <MapPin size={14} className="-ml-px shrink-0 text-emerald-700" aria-hidden="true" />
                <span className="min-w-0 flex-1">
                  <span className="block text-[10px] font-semibold uppercase tracking-[.12em] text-stone-400">Điểm đến</span>
                  <select aria-label="Điểm đến" value={destinationId} onChange={event => setDestinationId(event.target.value)} disabled={loading} className="w-full cursor-pointer appearance-none bg-transparent py-1 pr-3 text-sm font-semibold text-stone-800 outline-none">
                    {places.map(place => <option key={place.id} value={place.id}>{place.name}</option>)}
                  </select>
                </span>
                <ChevronDown size={15} className="shrink-0 text-stone-400" aria-hidden="true" />
              </label>
            </div>

            <div className="mt-3 flex flex-wrap gap-2" aria-label="Điểm đến gợi ý">
              {places.filter(place => place.id !== pickupId).slice(0, 3).map(place => <button key={place.id} type="button" disabled={loading} onClick={() => setDestinationId(place.id)} className={`flex items-center gap-1.5 rounded-full border px-2.5 py-1.5 text-[10px] font-medium transition ${destinationId === place.id ? 'border-emerald-200 bg-emerald-50 text-emerald-800' : 'border-stone-200 bg-white text-stone-600 hover:border-emerald-300'}`}><MapPin size={11} aria-hidden="true" />{place.name}</button>)}
            </div>

            <div className="section-divider my-6" />
            <div className="mb-3 flex items-center justify-between"><h2 className="text-xs font-semibold text-stone-700">Thanh toán</h2><span className="text-[10px] text-stone-400">Phương thức mô phỏng</span></div>
            <div className="grid grid-cols-2 gap-2" role="group" aria-label="Phương thức thanh toán">
              {([{ value: 'wallet', label: 'Ví SwiftPay', Icon: Wallet }, { value: 'card', label: 'Thẻ demo', Icon: CreditCard }] as const).map(({ value, label, Icon }) => <button key={value} type="button" disabled={loading} aria-pressed={paymentMethod === value} onClick={() => setPaymentMethod(value)} className={`flex min-h-12 items-center gap-2 rounded-xl border px-3 text-xs font-medium transition ${paymentMethod === value ? 'border-emerald-600 bg-emerald-50 text-emerald-800' : 'border-stone-200 text-stone-500 hover:border-emerald-300'}`}><Icon size={17} aria-hidden="true" /><span className="flex-1 text-left">{label}</span>{paymentMethod === value && <Check size={14} aria-hidden="true" />}</button>)}
            </div>

            {!online && <p className="mt-4 rounded-xl bg-amber-50 p-3 text-xs leading-5 text-amber-800">Tài xế đang ngoại tuyến. Chuyển sang giao diện tài xế và bật nhận chuyến để trải nghiệm đặt xe.</p>}
            {error && <p role="alert" className="error-message mt-4">{error}</p>}
            <button type="submit" disabled={loading || !pickupId || !destinationId || pickupId === destinationId} className="primary-button mt-5 flex w-full items-center justify-center gap-2">{loading ? <LoaderCircle size={17} className="animate-spin" aria-hidden="true" /> : <Navigation size={17} aria-hidden="true" />}<span>{loading ? 'Đang tìm chuyến phù hợp…' : 'Xem giá chuyến đi'}</span>{!loading && <ArrowRight size={17} className="ml-auto" aria-hidden="true" />}</button>
            {pickupId === destinationId && <p className="mt-2 text-xs text-amber-800">Vui lòng chọn điểm đến khác điểm đón.</p>}
          </form>

          <div className="mt-6 flex items-start gap-3 rounded-2xl border border-emerald-100 bg-emerald-50/60 p-4"><span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-xl bg-white text-emerald-700"><Leaf size={18} aria-hidden="true" /></span><div><p className="text-xs font-semibold text-emerald-900">Đi nhẹ nhàng. Đến an tâm.</p><p className="mt-1 text-[11px] leading-5 text-emerald-800/70">Giá rõ ràng trước khi đặt.<br />Điểm đón và điểm đến rõ ràng trên bản đồ.</p></div></div>
        </>
      ) : (
        <>
          <span className={`status-pill mb-4 inline-flex items-center gap-1.5 ${trip.status === 'Paid' ? 'bg-emerald-100 text-emerald-800' : ''}`}>{trip.status === 'Paid' ? <CheckCircle2 size={13} aria-hidden="true" /> : <span className="h-1.5 w-1.5 rounded-full bg-current" />}{quoted ? 'GIÁ ƯỚC TÍNH' : terminal ? 'CHUYẾN ĐI ĐÃ KẾT THÚC' : 'CHUYẾN ĐI HIỆN TẠI'}</span>
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
