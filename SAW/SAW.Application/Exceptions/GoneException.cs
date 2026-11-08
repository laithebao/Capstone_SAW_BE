namespace SAW.Application.Exceptions;

/// <summary>A previously available resource is no longer available. Maps to HTTP 410.</summary>
public sealed class GoneException(string message) : Exception(message);
