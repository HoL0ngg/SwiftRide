using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Application.Exceptions;
using SwiftRide.TripService.Application.Reliability;

namespace SwiftRide.TripService.Infrastructure.Http;

public sealed class MatchingClient : IMatchingClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;

    public MatchingClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (_httpClient.BaseAddress is null || !_httpClient.BaseAddress.IsAbsoluteUri ||
            (_httpClient.BaseAddress.Scheme != Uri.UriSchemeHttp && _httpClient.BaseAddress.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("BaseAddress must be an absolute HTTP or HTTPS URI.", nameof(httpClient));
        }
    }

    public async Task<MatchOutcome> MatchAsync(MatchInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var payload = new
        {
            latitude = input.Pickup.Latitude,
            longitude = input.Pickup.Longitude,
            excludedDriverIds = input.ExcludedDriverIds
        };

        var attempts = 0;
        const int maxAttempts = 2; // 1 initial + max 1 retry transient

        while (true)
        {
            attempts++;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                using var response = await _httpClient.PostAsJsonAsync("/match", payload, JsonOptions, linkedCts.Token);

                if (response.IsSuccessStatusCode)
                {
                    MatchResultDto? result;
                    try
                    {
                        result = await response.Content.ReadFromJsonAsync<MatchResultDto>(JsonOptions, linkedCts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new ServiceClientException("Failed to deserialize Match response.", ex);
                    }

                    if (result is null)
                    {
                        throw new ServiceClientException("Match response was empty.");
                    }

                    var status = string.Equals(result.Status, "Matched", StringComparison.OrdinalIgnoreCase)
                        ? MatchStatus.Matched
                        : MatchStatus.NoDriverAvailable;

                    var candidate = result.Candidate is not null
                        ? new CandidateDriver(result.Candidate.DriverId, result.Candidate.DistanceKm)
                        : null;

                    return new MatchOutcome(status, candidate);
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    throw new ServiceClientAuthException($"Match endpoint returned {(int)response.StatusCode}.");
                }

                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    throw new ServiceClientConflictException("Match endpoint returned Conflict.");
                }

                var isTransient = response.StatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    or HttpStatusCode.InternalServerError
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout;

                if (isTransient && attempts < maxAttempts)
                {
                    await Task.Delay(RetrySchedule.NextDelay(attempts, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)), ct);
                    continue;
                }

                if (isTransient)
                {
                    throw new ServiceClientTransientException($"Match request failed with transient status {(int)response.StatusCode}.");
                }

                throw new ServiceClientException($"Match request failed with status {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                if (attempts < maxAttempts)
                {
                    await Task.Delay(RetrySchedule.NextDelay(attempts, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)), ct);
                    continue;
                }
                throw new ServiceClientTransientException("Match request timed out.");
            }
            catch (HttpRequestException ex)
            {
                if (attempts < maxAttempts)
                {
                    await Task.Delay(RetrySchedule.NextDelay(attempts, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2)), ct);
                    continue;
                }
                throw new ServiceClientTransientException("Match request failed due to network error.", ex);
            }
        }
    }

    public async Task<QuotedFare> QuoteAsync(QuoteInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var payload = new
        {
            pickup = new { latitude = input.Pickup.Latitude, longitude = input.Pickup.Longitude },
            destination = new { latitude = input.Destination.Latitude, longitude = input.Destination.Longitude },
            mode = input.Mode
        };

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            using var response = await _httpClient.PostAsJsonAsync("/quote", payload, JsonOptions, linkedCts.Token);

            if (response.IsSuccessStatusCode)
            {
                QuoteResultDto? result;
                try
                {
                    result = await response.Content.ReadFromJsonAsync<QuoteResultDto>(JsonOptions, linkedCts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new ServiceClientException("Failed to deserialize Quote response.", ex);
                }

                if (result is null)
                {
                    throw new ServiceClientException("Quote response was empty.");
                }

                return new QuotedFare(
                    result.QuoteId,
                    result.Amount,
                    result.Currency,
                    result.DistanceKm,
                    result.EstimatedMinutes,
                    result.CreatedAt);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ServiceClientAuthException($"Quote endpoint returned {(int)response.StatusCode}.");
            }

            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                throw new ServiceClientConflictException("Quote endpoint returned Conflict.");
            }

            var isTransient = response.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;

            if (isTransient)
            {
                throw new ServiceClientTransientException($"Quote request failed with status {(int)response.StatusCode}.");
            }

            throw new ServiceClientException($"Quote request failed with status {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new ServiceClientTransientException("Quote request timed out.");
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceClientTransientException("Quote request network error.", ex);
        }
    }

    private sealed record CandidateDto(string DriverId, double DistanceKm);
    private sealed record MatchResultDto(string Status, CandidateDto? Candidate);
    private sealed record QuoteResultDto(Guid QuoteId, decimal Amount, string Currency, decimal DistanceKm, decimal EstimatedMinutes, DateTimeOffset CreatedAt);
}
