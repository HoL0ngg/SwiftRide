import { useState } from 'react'
import { ArrowRight, Check, CheckCheck, CircleHelp, Clock3, Compass, ExternalLink, Flag, LoaderCircle, MapPin, Navigation, Power, Route, ShieldCheck, Sparkles, Wallet } from 'lucide-react'
import { demoApi, formatMoney, useDemo } from './demo'

export default function DriverView() {
  const { trip, history, online } = useDemo()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const completed = history.filter((ride) => ride.status === 'Paid')
  const totalValue = completed.reduce((total, ride) => total + ride.fare, 0)
  const hasAcceptedTrip = !!trip && ['DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending'].includes(trip.status)
  const hasRide = !!trip && ['DriverOffered', 'DriverAccepted', 'EnRoute', 'PickedUp', 'DroppedOff', 'PaymentPending', 'Paid'].includes(trip.status)

  async function run(action: () => Promise<unknown>) {
    if (busy) return
    setBusy(true)
    setError('')
    try {
      await action()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Chưa thể thực hiện thao tác. Vui lòng thử lại.')
    } finally {
      setBusy(false)
    }
  }

  const nextAction = trip?.status === 'DriverAccepted'
    ? { label: 'Bắt đầu đến đón', action: () => demoApi.enRoute(), icon: Navigation }
    : trip?.status === 'EnRoute'
      ? { label: 'Đã đón khách', action: () => demoApi.pickup(), icon: Check }
      : trip?.status === 'PickedUp'
        ? { label: 'Đã trả khách', action: () => demoApi.dropoff(), icon: Flag }
        : null

  const rideHeading = trip?.status === 'DriverOffered' ? 'Một chuyến đi mới.'
    : trip?.status === 'DriverAccepted' ? 'Sẵn sàng đón khách.'
      : trip?.status === 'EnRoute' ? 'Đang đến điểm đón.'
        : trip?.status === 'PickedUp' ? 'Chúc bạn lái xe an toàn.'
          : trip?.status === 'Paid' ? 'Hành trình trọn vẹn!'
            : 'Đã đến nơi an toàn.'

  return (
    <section className="flex min-h-full flex-col" aria-label="Không gian tài xế">
      <div className="mb-7 flex items-center justify-between gap-3">
        <span className="eyebrow flex items-center gap-2"><Compass size={15} aria-hidden="true" /> ĐỐI TÁC SWIFTRIDE</span>
        <span className="rounded-full bg-emerald-50 px-3 py-1.5 text-[11px] font-semibold text-emerald-800">Chế độ demo</span>
      </div>

      <div className="mb-6 flex items-start justify-between gap-4">
        <div>
          <h2 className="panel-title">{hasRide ? rideHeading : <>Cầm lái hôm nay.<br />Kết nối hành trình.</>}</h2>
          <p className="muted mt-3 text-sm leading-relaxed">{hasRide ? 'Mỗi chặng đường, một trải nghiệm tốt hơn.' : 'Chủ động thời gian. Sẵn sàng cho chuyến đi mới.'}</p>
        </div>
      </div>

      <div className="mb-6 flex items-center justify-between gap-4 rounded-2xl border border-slate-200 bg-white p-4">
        <div className="flex items-center gap-3">
          <span className={`flex h-10 w-10 items-center justify-center rounded-full ${online ? 'bg-emerald-50 text-emerald-700' : 'bg-slate-100 text-slate-500'}`}><Power size={19} aria-hidden="true" /></span>
          <div><p className="text-sm font-semibold text-slate-900">{online ? 'Bạn đang trực tuyến' : 'Bạn đang ngoại tuyến'}</p><p className="mt-1 text-xs text-slate-500">{hasAcceptedTrip ? 'Hoàn thành chuyến trước khi nghỉ' : online ? 'Sẵn sàng nhận lời mời chuyến' : 'Bật để bắt đầu nhận chuyến'}</p></div>
        </div>
        <button type="button" role="switch" aria-checked={online} aria-label="Trạng thái nhận chuyến" disabled={busy || hasAcceptedTrip} onClick={() => void run(() => demoApi.setOnline(!online))} className={`relative h-7 w-12 shrink-0 rounded-full transition-colors disabled:cursor-not-allowed disabled:opacity-60 ${online ? 'bg-emerald-700' : 'bg-slate-300'}`}><span className={`absolute top-1 h-5 w-5 rounded-full bg-white shadow-sm transition-transform ${online ? 'left-1 translate-x-5' : 'left-1 translate-x-0'}`} /></button>
      </div>

      {error && <p role="alert" className="error-message mb-4">{error}</p>}

      {hasRide && trip ? (
        <div aria-live="polite" className="rounded-[24px] border border-slate-200 bg-white p-5 shadow-[0_8px_30px_-20px_rgba(15,23,42,0.25)]">
          <div className="mb-5 flex items-center justify-between gap-2">
            <span className={`status-pill ${trip.status === 'Paid' ? 'bg-emerald-100 text-emerald-800' : 'bg-emerald-50 text-emerald-700'}`}>
              {trip.status === 'DriverOffered' ? <><span className="h-1.5 w-1.5 rounded-full bg-emerald-600" /> Lời mời chuyến mới</> : trip.status === 'Paid' ? <><CheckCheck size={14} /> Đã thanh toán</> : trip.status === 'PaymentPending' || trip.status === 'DroppedOff' ? <><Clock3 size={14} /> Đang xử lý thanh toán</> : <><Navigation size={14} /> Chuyến đang thực hiện</>}
            </span>
            <span className="text-[11px] font-medium text-slate-400">#{trip.id.slice(-6).toUpperCase()}</span>
          </div>

          <div className="mb-6 flex items-end justify-between gap-2">
            <div><p className="mb-1 text-xs text-slate-500">Giá chuyến đã xác nhận</p><p className="text-[32px] font-bold leading-tight tracking-tight text-slate-900">{formatMoney(trip.fare)}</p></div>
            <span className="mb-1 flex items-center gap-1.5 rounded-lg bg-slate-50 px-2 py-1.5 text-xs text-slate-600"><Wallet size={14} aria-hidden="true" /> {trip.paymentMethod === 'wallet' ? 'Ví điện tử' : 'Thẻ'}</span>
          </div>

          <div className="relative mb-5 space-y-6">
            <span className="absolute bottom-7 left-[9px] top-6 border-l border-dashed border-slate-300" aria-hidden="true" />
            <div className="relative flex gap-3"><span className="mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded-full bg-emerald-50"><span className="h-2 w-2 rounded-full bg-emerald-600" /></span><div><p className="mb-1 text-[10px] font-semibold uppercase tracking-[.12em] text-slate-400">Điểm đón</p><p className="text-sm font-semibold text-slate-900">{trip.pickup.name}</p><p className="mt-1 text-xs leading-relaxed text-slate-500">{trip.pickup.address}</p></div></div>
            <div className="relative flex gap-3"><span className="mt-1 flex h-5 w-5 shrink-0 items-center justify-center rounded-md bg-orange-50 text-orange-600"><MapPin size={14} aria-hidden="true" /></span><div><p className="mb-1 text-[10px] font-semibold uppercase tracking-[.12em] text-slate-400">Điểm đến</p><p className="text-sm font-semibold text-slate-900">{trip.destination.name}</p><p className="mt-1 text-xs leading-relaxed text-slate-500">{trip.destination.address}</p></div></div>
          </div>

          <div className="mb-5 flex items-center gap-5 border-y border-slate-100 py-3 text-xs text-slate-600"><span className="flex items-center gap-2"><Route size={15} aria-hidden="true" /> {trip.distanceKm.toLocaleString('vi-VN', { maximumFractionDigits: 1 })} km</span><span className="flex items-center gap-2"><Clock3 size={15} aria-hidden="true" /> Khoảng {trip.durationMinutes} phút</span></div>

          {trip.status === 'DriverOffered' && <>
            {trip.rejections > 0 && <p className="mb-4 text-xs leading-relaxed text-slate-500">Mô phỏng ghép lại tài xế · Đã từ chối {trip.rejections} lần.</p>}
            <div className="flex gap-3"><button className="secondary-button flex-1" disabled={busy} onClick={() => void run(() => demoApi.driverReject())}>Từ chối</button><button className="primary-button flex-[1.7]" disabled={busy || !online} onClick={() => void run(() => demoApi.driverAccept())}>{busy ? <LoaderCircle size={17} className="animate-spin" aria-hidden="true" /> : <Check size={18} aria-hidden="true" />} Nhận chuyến</button></div>
            {!online && <p className="mt-3 text-center text-xs text-slate-500">Bật trực tuyến để nhận chuyến này.</p>}
          </>}

          {nextAction && <><p className="mb-4 text-xs leading-relaxed text-slate-500">{trip.status === 'DriverAccepted' ? 'Bắt đầu di chuyển khi bạn đã sẵn sàng.' : trip.status === 'EnRoute' ? 'Chỉ xác nhận khi hành khách đã lên xe.' : 'Chỉ xác nhận khi đã đưa hành khách đến điểm đến.'}</p><button className="primary-button w-full" disabled={busy} onClick={() => void run(nextAction.action)}>{busy ? <LoaderCircle size={17} className="animate-spin" aria-hidden="true" /> : <nextAction.icon size={18} aria-hidden="true" />} {nextAction.label} <ArrowRight size={17} aria-hidden="true" /></button></>}

          {(trip.status === 'PaymentPending' || trip.status === 'DroppedOff') && <div className="rounded-xl bg-amber-50 p-4"><p className="text-sm font-semibold text-amber-900">Đã trả khách, đang chờ thanh toán</p><p className="mt-2 text-xs leading-relaxed text-amber-800">Chuyến chỉ hoàn tất khi thanh toán được xác nhận thành công.</p><button className="secondary-button mt-4 w-full bg-white" disabled={busy} onClick={() => void run(() => demoApi.settlePayment())}>{busy && <LoaderCircle size={16} className="animate-spin" aria-hidden="true" />} Mô phỏng thanh toán thành công</button></div>}

          {trip.status === 'Paid' && <><div className="mb-5 flex items-start gap-3 rounded-xl bg-emerald-50 p-4"><ShieldCheck className="mt-0.5 shrink-0 text-emerald-700" size={19} aria-hidden="true" /><div><p className="text-sm font-semibold text-emerald-900">Cảm ơn bạn đã đồng hành!</p><p className="mt-1 text-xs leading-relaxed text-emerald-700">Thanh toán demo đã được xác nhận. Chuyến đi được lưu trong lịch sử.</p></div></div><a className="primary-button w-full" href="/?role=rider" target="_blank" rel="noopener noreferrer">Mở hành khách đặt chuyến mới <ExternalLink size={15} aria-hidden="true" /><span className="sr-only"> (mở tab mới)</span></a></>}
        </div>
      ) : (
        <>
          <div className="relative overflow-hidden rounded-[24px] bg-[#eaf4ef] px-6 pb-6 pt-7 text-center">
            <div className="relative mx-auto mb-5 flex h-32 w-32 items-center justify-center">
              <span className="absolute inset-0 rounded-full border border-emerald-700/10" /><span className="absolute inset-4 rounded-full border border-emerald-700/15" /><span className="absolute inset-8 rounded-full bg-white/60" />
              <span className="relative flex h-14 w-14 items-center justify-center rounded-2xl bg-emerald-800 text-white shadow-lg shadow-emerald-900/15"><Navigation size={27} strokeWidth={1.8} aria-hidden="true" /></span>
              <span className="absolute right-1 top-6 h-2 w-2 rounded-full bg-emerald-500" /><span className="absolute bottom-5 left-2 h-2 w-2 rounded-full bg-emerald-700/30" />
            </div>
            <h2 className="text-lg font-semibold tracking-tight text-emerald-950">{online ? 'Sẵn sàng đón cơ hội mới' : 'Một nhịp nghỉ, rồi đi tiếp'}</h2>
            <p className="mx-auto mt-2 max-w-64 text-xs leading-6 text-emerald-900/65">{online ? 'Lời mời chuyến sẽ xuất hiện tại đây sau khi hành khách xác nhận giá.' : 'Bật trạng thái trực tuyến khi bạn đã sẵn sàng đồng hành cùng hành khách.'}</p>
            <div className="mt-5 flex items-center justify-center gap-2 text-[11px] font-medium text-emerald-800"><span className={`h-1.5 w-1.5 rounded-full ${online ? 'bg-emerald-600' : 'bg-slate-400'}`} /> {online ? 'Đang chờ yêu cầu chuyến' : 'Tạm dừng nhận chuyến'}</div>
          </div>

          {(trip?.status === 'Cancelled' || trip?.status === 'Failed') && <p className="mt-4 rounded-xl border border-slate-200 bg-white p-4 text-xs leading-relaxed text-slate-600" role="status">{trip.status === 'Cancelled' ? 'Hành khách đã hủy chuyến. Bạn có thể chờ yêu cầu mới.' : 'Chưa thể ghép chuyến sau giới hạn từ chối. Hành khách có thể tạo yêu cầu mới.'}</p>}

          <div className="mt-5 flex items-start gap-3 rounded-2xl border border-slate-200 bg-white p-4"><span className="mt-0.5 text-emerald-700"><Sparkles size={18} aria-hidden="true" /></span><div><h3 className="text-sm font-semibold text-slate-800">Thử một chuyến đi cùng SwiftRide</h3><p className="mt-1.5 text-xs leading-relaxed text-slate-500">Mở giao diện hành khách ở tab khác, đặt chuyến và xác nhận giá. Lời mời sẽ xuất hiện ở đây.</p><a href="/?role=rider" target="_blank" rel="noopener noreferrer" className="mt-3 inline-flex min-h-9 items-center gap-2 text-xs font-semibold text-emerald-800 hover:text-emerald-600">Mở giao diện hành khách <ExternalLink size={13} aria-hidden="true" /><span className="sr-only"> (mở tab mới)</span></a></div></div>
        </>
      )}

      <div className="mb-6 mt-7">
        <div className="mb-3 flex items-center justify-between"><h2 className="text-sm font-semibold text-slate-900">Hành trình của bạn</h2><span className="text-[10px] text-slate-400">Phiên demo hiện tại</span></div>
        <div className="grid grid-cols-2 gap-3"><div className="rounded-2xl border border-slate-200 bg-white p-4"><div className="mb-3 flex items-center gap-2 text-slate-500"><Flag size={15} aria-hidden="true" /><span className="text-[11px]">Chuyến hoàn tất</span></div><p className="text-2xl font-bold tracking-tight text-slate-900">{completed.length}<span className="ml-1.5 text-xs font-normal text-slate-400">chuyến</span></p></div><div className="rounded-2xl border border-slate-200 bg-white p-4"><div className="mb-3 flex items-center gap-2 text-slate-500"><Wallet size={15} aria-hidden="true" /><span className="text-[11px]">Giá trị chuyến</span></div><p className="text-xl font-bold tracking-tight text-emerald-800">{formatMoney(totalValue)}</p></div></div>
      </div>

      <div className="mt-auto flex items-start gap-2.5 border-t border-slate-200/80 pt-5 text-[11px] leading-relaxed text-slate-400"><CircleHelp className="mt-0.5 shrink-0" size={15} aria-hidden="true" /><p>Đây là trải nghiệm mô phỏng. Không phát sinh giao dịch hoặc thu nhập thực tế.</p></div>
    </section>
  )
}
