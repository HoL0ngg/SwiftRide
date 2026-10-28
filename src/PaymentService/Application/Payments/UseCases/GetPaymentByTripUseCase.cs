using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Domain.Repositories;
namespace SwiftRide.PaymentService.Application.Payments.UseCases;

public sealed class GetPaymentByTripUseCase(IPaymentReader reader)
{
    public async Task<PaymentDto> ExecuteAsync(Guid tripId, CancellationToken ct)
    {
        var payment = await reader.GetByTripIdAsync(tripId, ct) ?? throw new PaymentNotFoundException(tripId);
        return PaymentMapper.ToDto(payment);
    }
}