## Overview
This is an upgraded version of the default Netick LiteNetLib, enabling users to perform NAT punchthrough to establish direct connections.

## Features
- NAT Punch
- Host (LAN) Discovery

## Installation

### Prerequisites

Unity Editor version 2021 or later.

Install Netick 2 before installing this package.
https://github.com/NetickNetworking/NetickForUnity

### Steps

- Open the Unity Package Manager by navigating to Window > Package Manager along the top bar.
- Click the plus icon.
- Select Add package from git URL
- Enter `https://github.com/StinkySteak/NetickLiteNetLibNatPunchTransport.git`
- You can then create an instance by by double clicking in the Assets folder and going
 - Create > Netick > Transport > LiteNetLibNatPunchTransportProvider

## How to Use?
Look out for Nat Puncher Address and Port field on the transport Provider, these are the key components to register and or request NAT IP.  
In dev, staging, or production you can try the public NAT Puncher Relay `lnl-puncher.netick.net:6956`. If you want to self-host the NAT Puncher STUN server, Let's talk

## Host Discovery - How to?
1. Create a script implementing `ILNLDiscovery`
1. Create a LNLDiscovery instance, and Initialize it.
1. Ensure to PollUpdate the LNLDiscovery
1. Make sure to Stop the LNLDiscover after it is not used (Can be used inside OnDestroy). Otherwise it would leak

Example Code
```cs
public class LNLDiscoveryMono : MonoBehaviour, ILNLDiscoveryListener
{
    [SerializeField] private int _startPort;
    [SerializeField] private int _endPort;

    private LNLDiscovery _discovery;

    private void Start()
    {
        _discovery = new LNLDiscovery(this, 5555, _startPort, _endPort);
        _discovery.Initialize();
    }

    [ContextMenu(nameof(Discover))]
    private void Discover()
    {
        _discovery.DiscoverHost();
    }

    private void Update()
    {
        _discovery.PollUpdate();
    }

    public void OnDiscoveredSessionUpdated(IReadOnlyList<DiscoveredSession> sessions)
    {
        Debug.Log("OnDiscoveredSessionUpdated");

        for (int i = 0; i < sessions.Count; i++)
        {
            Debug.Log($"host: {sessions[i].HostName} endpoint: {sessions[i].EndPoint}");
        }
    }
}

```
