namespace Meshline.Tests.Support;

internal static class RequestQuery
{
    public static Dictionary<string, string> Parse(ObservedRequest request) => request.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
}
