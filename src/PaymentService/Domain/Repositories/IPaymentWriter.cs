using SwiftRide.PaymentService.Domain.Entities;
namespace SwiftRide.PaymentService.Domain.Repositories;

public interface IPaymentWriter { void Add(Payment payment); }   // 7.2 Chỉ "đánh dấu thêm"; lưu thật do IUnitOfWork