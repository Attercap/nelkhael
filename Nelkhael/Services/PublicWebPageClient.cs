using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nelkhael.Services;

public sealed class PublicWebPageClient(HttpClient httpClient)
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumRedirects = 5;

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectCallback = ConnectToPublicAddressAsync,
        UseCookies = false,
        UseProxy = false
    };

    public static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidDataException("URL must be an absolute HTTP or HTTPS address.");
        }

        return ValidateUri(uri).AbsoluteUri;
    }

    public async Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken)
    {
        var currentUri = new Uri(NormalizeUrl(url), UriKind.Absolute);
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd("Nelkhael/1.0 (+https://github.com/Attercap/nelkhael)");
            request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml");
            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount >= MaximumRedirects || response.Headers.Location is null)
                {
                    throw new InvalidDataException("The page redirected too many times or returned a redirect without a location.");
                }

                currentUri = ValidateUri(new Uri(currentUri, response.Headers.Location));
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new HttpRequestException(
                        "The website denied automated access (403 Forbidden). Nelkhael will not bypass the restriction; use a page the site permits automated access to, or a local copy if its terms allow it.",
                        null,
                        response.StatusCode);
                }

                throw new HttpRequestException(
                    $"Page request returned {(int)response.StatusCode} ({response.ReasonPhrase}).",
                    null,
                    response.StatusCode);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null
                && !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
                && !mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Page returned unsupported content type '{mediaType}'.");
            }

            return await ReadHtmlAsync(response.Content, cancellationToken);
        }
    }

    private static async Task<string> ReadHtmlAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new InvalidDataException($"HTML page exceeds the {MaximumResponseBytes}-byte size limit.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(bytes.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException($"HTML page exceeds the {MaximumResponseBytes}-byte size limit.");
            }

            await buffer.WriteAsync(bytes.AsMemory(0, read), cancellationToken);
        }

        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        var encoding = string.IsNullOrWhiteSpace(charset)
            ? Encoding.UTF8
            : Encoding.GetEncoding(charset);
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static Uri ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only public HTTP or HTTPS URLs on their standard ports can be indexed.");
        }

        if (IPAddress.TryParse(uri.Host, out var address) && !IsPublicAddress(address))
        {
            throw new InvalidDataException("URLs pointing to non-public IP addresses cannot be indexed.");
        }

        var builder = new UriBuilder(uri) { Fragment = "" };
        return builder.Uri;
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new HttpRequestException("The page host does not resolve exclusively to public IP addresses.");
        }

        SocketException? lastSocketException = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            var connected = false;
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    cancellationToken);
                connected = true;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                lastSocketException = exception;
            }
            finally
            {
                if (!connected)
                {
                    socket.Dispose();
                }
            }
        }

        throw new HttpRequestException("Could not connect to the public page host.", lastSocketException);
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublicAddress(address.MapToIPv4());
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var isGlobalUnicast = (bytes[0] & 0xE0) == 0x20;
            var isSpecialPurpose2001 = bytes[0] == 0x20
                && bytes[1] == 0x01
                && bytes[2] <= 0x01;
            var isDocumentation = bytes[0] == 0x20
                && bytes[1] == 0x01
                && bytes[2] == 0x0D
                && bytes[3] == 0xB8;
            var is6To4 = bytes[0] == 0x20 && bytes[1] == 0x02;
            return isGlobalUnicast && !isSpecialPurpose2001 && !isDocumentation && !is6To4;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var first = bytes[0];
        var second = bytes[1];
        var third = bytes[2];
        return first != 0
            && first != 10
            && !(first == 100 && second >= 64 && second <= 127)
            && first != 127
            && !(first == 169 && second == 254)
            && !(first == 172 && second >= 16 && second <= 31)
            && !(first == 192 && second == 0 && third == 0)
            && !(first == 192 && second == 0 && third == 2)
            && !(first == 192 && second == 88 && third == 99)
            && !(first == 192 && second == 168)
            && !(first == 198 && (second == 18 || second == 19))
            && !(first == 198 && second == 51 && third == 100)
            && !(first == 203 && second == 0 && third == 113)
            && first < 224;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
