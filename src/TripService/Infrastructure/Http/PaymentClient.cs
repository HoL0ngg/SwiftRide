using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;

namespace SwiftRide.TripService.Infrastructure.Http;

public sealed class PaymentClient : IPaymentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;

    public PaymentClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is null || !_httpClient.BaseAddress.IsAbsoluteUri ||
            (_httpClient.BaseAddress.Scheme != Uri.UriSchemeHttp && _httpClient.BaseAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("BaseAddress must be an absolute HTTP or HTTPS URI.", nameof(httpClient));
        }
    }

    public async Task<PaymentObservation?> GetByTripAsync(Guid tripId, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var response = await _httpClient.GetAsync($"/payments?tripId={tripId:D}", linkedCts.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (response.IsSuccessStatusCode)
            {
                PaymentDto? dto;
                try
                {
                    dto = await response.Content.ReadFromJsonAsync<PaymentDto>(JsonOptions, linkedCts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new ServiceClientException("Failed to deserialize Payment response.", ex);
                }

                if (dto is null)
                {
                    throw new ServiceClientException("Payment response was empty.");
                }

                return MapObservation(dto, response);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ServiceClientAuthException($"Payment endpoint returned {(int)response.StatusCode}.");
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new ServiceClientConflictException("Payment endpoint returned Conflict.");
            }

            var isTransient = response.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

            if (isTransient)
            {
                throw new ServiceClientTransientException($"Payment query failed with status {(int)response.StatusCode}.");
            }

            throw new ServiceClientException($"Payment query failed with status {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new ServiceClientTransientException("Payment query timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceClientTransientException("Payment query network error.", ex);
        }
    }

    public async Task<PaymentObservation> ChargeAsync(ChargeRequest input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var payload = new
        {
            tripId = input.TripId,
            riderId = input.RiderId,
            driverId = input.DriverId,
            amount = input.Amount,
            currency = input.Currency,
            method = input.Method,
            paymentToken = input.PaymentToken
        };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/payments")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };

            if (!string.IsNullOrWhiteSpace(input.IdempotencyKey))
            {
                request.Headers.Add("Idempotency-Key", input.IdempotencyKey);
            }

            using var response = await _httpClient.SendAsync(request, linkedCts.Token);

            if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted)
            {
                PaymentDto? dto;
                try
                {
                    dto = await response.Content.ReadFromJsonAsync<PaymentDto>(JsonOptions, linkedCts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new ServiceClientException("Failed to deserialize Charge response.", ex);
                }

                if (dto is null)
                {
                    throw new ServiceClientException("Charge response was empty.");
                }

                return MapObservation(dto, response);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ServiceClientAuthException($"Charge endpoint returned {(int)response.StatusCode}.");
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new ServiceClientConflictException("Charge endpoint returned Conflict.");
            }

            var isTransient = response.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

            if (isTransient)
            {
                throw new ServiceClientTransientException($"Charge request failed with status {(int)response.StatusCode}.");
            }

            throw new ServiceClientException($"Charge request failed with status {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new ServiceClientTransientException("Charge request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceClientTransientException("Charge request network error.", ex);
        }
    }

    private static PaymentObservation MapObservation(PaymentDto dto, HttpResponseMessage response)
    {
        var status = dto.Status?.ToLowerInvariant() switch
        {
            "settled" => RemotePaymentStatus.Settled,
            "failed" => RemotePaymentStatus.Failed,
            _ => RemotePaymentStatus.Pending
        };

        TimeSpan? retryAfter = null;
        if (response.Headers.RetryAfter is not null)
        {
            if (response.Headers.RetryAfter.Delta.HasValue)
            {
                retryAfter = response.Headers.RetryAfter.Delta.Value;
            }
            else if (response.Headers.RetryAfter.Date.HasValue)
            {
                var diff = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                if (diff > TimeSpan.Zero)
                {
                    retryAfter = diff;
                }
            }
        }

        var hasSettledRefund = dto.Refund is not null &&
            string.Equals(dto.Refund.Status, "Settled", StringComparison.OrdinalIgnoreCase);

        PaymentProcessingObservation? processing = null;
        if (dto.Processing is not null)
        {
            processing = new PaymentProcessingObservation(
                dto.Processing.AttemptsStarted,
                dto.Processing.LeaseUntilUtc,
                dto.Processing.CanRetry,
                dto.Processing.RequiresAction);
        }

        return new PaymentObservation(
            dto.Id,
            dto.TripId,
            dto.RiderId,
            dto.DriverId,
            dto.Amount,
            dto.Currency,
            status,
            dto.FailureCode,
            hasSettledRefund,
            processing,
            retryAfter);
    }

    private sealed record RefundDto(Guid Id, decimal Amount, string Currency, string Reason, string Status);
    private sealed record ProcessingDto(int AttemptsStarted, DateTimeOffset? LeaseUntilUtc, bool CanRetry, bool RequiresAction);
    private sealed record PaymentDto(
        Guid Id,
        Guid TripId,
        string RiderId,
        string? DriverId,
        decimal Amount,
        string Currency,
        string Method,
        string Status,
        string? FailureCode,
        RefundDto? Refund,
        ProcessingDto? Processing);
}
