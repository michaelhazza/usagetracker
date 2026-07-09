using System.Net.Http;
using UsageWidget.Core.Security;

namespace UsageWidget.Core.Net;

/// <summary>
/// Turns a thrown transport failure into a specific, actionable, already-redacted message.
/// "Network or server error" tells the user nothing when the actual problem is a proxy that
/// needs sign-in or a VPN that dropped DNS — name the failure so they can fix it.
/// </summary>
public static class TransportError
{
    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException hre => hre.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError =>
                "Couldn't look up the server address (DNS) — check your internet, VPN, or proxy.",
            HttpRequestError.ConnectionError =>
                "Couldn't connect to the server — the network may be down or a proxy/firewall is blocking it.",
            HttpRequestError.SecureConnectionError =>
                "Secure connection failed (TLS) — a proxy or security software may be intercepting traffic.",
            HttpRequestError.ProxyTunnelError =>
                "The network proxy refused the connection — check your proxy settings.",
            _ => Redactor.Redact(hre.Message),
        },
        _ => Redactor.Redact(ex.Message),
    };
}
