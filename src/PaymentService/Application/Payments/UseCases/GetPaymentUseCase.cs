using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Domain.Repositories;
namespace SwiftRide.PaymentService.Application.Payments.UseCases;

public sealed class GetPaymentUseCase(IPaymentReader reader)   // ISP: chỉ cần Reader
{
    public async Task<PaymentDto> ExecuteAsync(Guid id, CancellationToken ct)
    {
        var payment = await reader.GetByIdAsync(id, ct) ?? throw new PaymentNotFoundException(id);
        return PaymentMapper.ToDto(payment);
    }
}
