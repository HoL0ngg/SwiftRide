namespace SwiftRide.TripService.Application.Exceptions;

public class ServiceClientException : Exception
{
    public ServiceClientException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public class ServiceClientTransientException : ServiceClientException
{
    public ServiceClientTransientException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public class ServiceClientConflictException : ServiceClientException
{
    public ServiceClientConflictException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}

public class ServiceClientAuthException : ServiceClientException
{
    public ServiceClientAuthException(string message, Exception? innerException = null)
        : base(message, innerException) { }
}
