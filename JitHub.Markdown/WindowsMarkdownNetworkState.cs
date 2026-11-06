using System;
using Windows.Networking.Connectivity;

namespace JitHub.Markdown;

internal sealed class WindowsMarkdownNetworkState : IMarkdownNetworkState
{
    public bool IsOnline
    {
        get
        {
            try
            {
                ConnectionProfile? profile = NetworkInformation.GetInternetConnectionProfile();
                return profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch
            {
                return true;
            }
        }
    }

    public bool IsMetered
    {
        get
        {
            try
            {
                ConnectionCost? cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
                return cost is not null &&
                    (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable ||
                     cost.Roaming ||
                     cost.OverDataLimit);
            }
            catch
            {
                return false;
            }
        }
    }
}
