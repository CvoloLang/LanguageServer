namespace Cvolo.LanguageServer.Tests.TestSupport;

internal static class JsonRpcFrames
{
    public static string Request(int id, string method, string? paramsJson)
    {
        return $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson ?? "null"}}}";
    }

    public static string Notification(string method, string? paramsJson)
    {
        return $"{{\"jsonrpc\":\"2.0\",\"method\":\"{method}\",\"params\":{paramsJson ?? "null"}}}";
    }

    public static string Cancel(int id)
    {
        return $"{{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{{\"id\":{id}}}}}";
    }
}