using System.Net;
using System.Text;

namespace MultiHostPz.App.Cloud;

public sealed record OAuthLoopbackResult(int StatusCode, string Message)
{
    public static OAuthLoopbackResult Success(string provider) =>
        new((int)HttpStatusCode.OK, $"{provider} connected successfully. You can close this window and return to MultiHostPz.");

    public static OAuthLoopbackResult Failure() =>
        new((int)HttpStatusCode.BadRequest, "Authorization failed. Return to MultiHostPz and try again.");
}

public static class OAuthLoopbackResponse
{
    public static async Task WriteAsync(HttpListenerResponse response, OAuthLoopbackResult result)
    {
        var bytes = Encoding.UTF8.GetBytes(result.Message);
        response.StatusCode = result.StatusCode;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentEncoding = Encoding.UTF8;
        response.ContentLength64 = bytes.Length;
        try
        {
            await response.OutputStream.WriteAsync(bytes);
        }
        finally
        {
            response.Close();
        }
    }
}
