namespace SwiftRide.TripService.Domain;

public class TripDomainException : Exception
{
    public TripDomainException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
