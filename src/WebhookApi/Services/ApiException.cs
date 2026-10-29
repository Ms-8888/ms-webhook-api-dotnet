namespace WebhookApi.Services;

/// <summary>Error that is rendered as {"detail": ...} with the given status code.</summary>
public class ApiException : Exception
{
    public int StatusCode { get; }
    public object Detail { get; }
    public IDictionary<string, string> Headers { get; }

    public ApiException(int statusCode, object detail, IDictionary<string, string>? headers = null)
        : base(detail as string ?? "api error")
    {
        StatusCode = statusCode;
        Detail = detail;
        Headers = headers ?? new Dictionary<string, string>();
    }

    public static ApiException Validation(string location, string field, string message) =>
        new(422, new[]
        {
            new { type = "value_error", loc = new[] { location, field }, msg = message }
        });
}
