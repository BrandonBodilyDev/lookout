namespace Lookout.Api.Live;

/// <summary>Domain failures that endpoints turn into HTTP problem responses.</summary>
public abstract class LookoutException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
    public object? Current { get; init; }
}

public sealed class NotFoundException(string what) : LookoutException($"{what} not found.", StatusCodes.Status404NotFound);

public sealed class ForbiddenActionException(string message) : LookoutException(message, StatusCodes.Status403Forbidden);

/// <summary>The caller's version is stale or the action does not fit the current state. The response carries the current state.</summary>
public sealed class ConflictException(string message) : LookoutException(message, StatusCodes.Status409Conflict);
