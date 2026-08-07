using System.Net.Http;
using DevDX.Models;

namespace DevDX.Services;

/// <summary>
/// The single gate for every outbound network call (design doc §21): no component may construct
/// an <see cref="HttpClient"/> directly; it refuses everything by default, and each capability is
/// separately consented and revocable. <b>Enforced by an architecture test</b>
/// (<c>DevDX.Tests.Services.NetworkPolicyArchitectureTests</c>) that scans the source tree for
/// <c>new HttpClient(</c> outside this file and fails the build if it finds one — the rule is
/// only real if it cannot be bypassed by forgetting it.
/// <para>
/// v1 has exactly one gated capability — the opt-in GitHub update check
/// (<see cref="UpdateService"/>) — because the only other networked feature the design document
/// specs (Currency Converter, §16) was parked as a post-v1 enhancement (see <see cref="ToolKind"/>).
/// The gate exists now so adding that capability later is a one-line change here, not a new pattern.
/// </para>
/// </summary>
public static class NetworkPolicy
{
    public static bool UpdateCheckAllowed(DockConfig config) => config.Network.UpdateCheck;

    /// <summary>True if nothing in the current config would permit any outbound call — asserted
    /// by <c>DevDX.Tests.Services.NetworkPolicyTests</c>.</summary>
    public static bool AllCapabilitiesDenied(DockConfig config) => !UpdateCheckAllowed(config);

    /// <summary>
    /// The only place an <see cref="HttpClient"/> is constructed for the update-check capability.
    /// Returns null when consent is withheld, so a caller cannot accidentally fetch anyway.
    /// </summary>
    public static HttpClient? CreateClientForUpdateCheck(DockConfig config)
    {
        if (!UpdateCheckAllowed(config))
            return null;
        return new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }
}
