using System;
using System.Globalization;
using oChan.Http;

namespace oChan.Desktop;

internal static class RequestStatus
{
    public static string? Format(RequestPolicyProgress? progress)
    {
        return progress switch
        {
            { Phase: RequestPolicyPhase.WaitingForAdmission } => "Waiting for request slot",
            { Phase: RequestPolicyPhase.Cooldown, Delay: TimeSpan delay } =>
                "Request cooldown (" + Math.Ceiling(delay.TotalSeconds).ToString("0", CultureInfo.CurrentCulture) + " s)",
            { Phase: RequestPolicyPhase.Cooldown } => "Request cooldown",
            { Phase: RequestPolicyPhase.Sending } => "Checking",
            _ => null
        };
    }
}
