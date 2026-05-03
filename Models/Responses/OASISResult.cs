namespace OASIS.WebAPI.Models.Responses;

public class OASISResult<T>
{
    public bool IsError { get; set; }
    public string Message { get; set; } = string.Empty;
    public T? Result { get; set; }
    public Exception? Exception { get; set; }
}

public class OASISResponse
{
    public bool IsError { get; set; }
    public string Message { get; set; } = string.Empty;
    public Exception? Exception { get; set; }
}
