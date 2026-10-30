using SwiftRide.PaymentService.Application.Abstractions;
using SwiftRide.PaymentService.Application.Exceptions;
using SwiftRide.PaymentService.Application.Payments.Dtos;
using SwiftRide.PaymentService.Domain.Repositories;

namespace SwiftRide.PaymentService.Application.Payments.UseCases;

public sealed class GetPaymentUseCase(IPaymentReader reader, IPaymentProcessingStore? processingStore = null)
{
    public async Task<PaymentDto> ExecuteAsync(Guid id, CancellationToken ct)
    {
        var payment = await reader.GetByIdAsync(id, ct) ?? throw new PaymentNotFoundException(id);
        PaymentProcessingDto? processing = null;
        string? fallbackDriver = null;
        if (processingStore is not null)
        {
            processing = await processingStore.GetAsync(payment.Id, ct);
            fallbackDriver = await processingStore.GetDriverIdAsync(payment.Id, ct);
        }
        return PaymentMapper.ToDto(payment, processing, fallbackDriver);
    }
}
