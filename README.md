## Overview
This is an upgraded version of the default Netick LiteNetLib, enabling users to perform NAT punchthrough to establish direct connections.

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
In dev, staging, or production you can try the public NAT Puncher Relay `lnl-puncher.netick.net:6956`. If you want to self-host the NAT Puncher Relay server, Let's talk