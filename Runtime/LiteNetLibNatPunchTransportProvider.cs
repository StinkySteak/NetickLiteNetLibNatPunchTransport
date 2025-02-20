using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections.Generic;
using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System;
using Netick.Unity;

namespace Netick.Transport
{
    [CreateAssetMenu(fileName = "LiteNetLibNatPunchTransportProvider", menuName = "Netick/Transport/LiteNetLibNatPunchTransportProvider", order = 1)]
    public class LiteNetLibNatPunchTransportProvider : NetworkTransportProvider
    {
        [Tooltip("Time duration (in seconds) until a connection is dropped when no packets were received.")]
        public float DisconnectTimeout = 5;
        [Tooltip("Time interval (in seconds) between connection attempts.")]
        public float ReconnectInterval = 0.5f;
        [Tooltip("Max number of connect attempts.")]
        public int MaxConnectAttempts = 10;
        [Tooltip("LiteNetLib internal logic update interval (in seconds).")]
        public float UpdateInterval = 0.015f;

        [Space]
        public string NatPuncherAddress;
        public int NatPuncherPort;
        public float NatPunchHeartbeat = 5f;
        [Tooltip("How long attempting to NAT Punch to the server as a client")]
        public float NatPunchTimeout = 3f;

        public override NetworkTransport MakeTransportInstance() => new LiteNetLibNatPunchTransport(this);
    }

    public class LiteNetLibNatPunchTransport : NetworkTransport, INetEventListener, INatPunchListener
    {
        public class LNLNATPunchConnection : TransportConnection
        {
            public LiteNetLibNatPunchTransport Transport;
            public NetPeer LNLPeer;
            public override IEndPoint EndPoint => LNLPeer.EndPoint.ToNetickEndPoint();
            public override int Mtu => LNLPeer.Mtu;
            public LNLNATPunchConnection(LiteNetLibNatPunchTransport transport)
            {
                Transport = transport;
            }
            public unsafe override void Send(IntPtr ptr, int length) => LNLPeer.Send(new ReadOnlySpan<byte>(ptr.ToPointer(), length), DeliveryMethod.Unreliable);
            public unsafe override void SendUserData(IntPtr ptr, int length, TransportDeliveryMethod method) => LNLPeer.Send(new ReadOnlySpan<byte>(ptr.ToPointer(), length), method == TransportDeliveryMethod.Unreliable ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableOrdered);
        }

        private LiteNetLibNatPunchTransportProvider _provider;
        private NetManager _netManager;
        private NetDataWriter _writer = new NetDataWriter();
        private BitBuffer _buffer;
        private byte[] _connectionBytes = new byte[200];
        private int _port;
        private Dictionary<NetPeer, LNLNATPunchConnection> _clients;
        private Queue<LNLNATPunchConnection> _freeClients;
        private UserNatPunchModule _userNatPunchModule;
        private ConnectParameter _queuedConnectParameter;

        public LiteNetLibNatPunchTransport(LiteNetLibNatPunchTransportProvider provider)
        {
            this._provider = provider;
        }

        public override void Init()
        {
            _clients = new(Engine.Config.MaxPlayers);
            _freeClients = new(Engine.Config.MaxPlayers);
            _buffer = new BitBuffer(createChunks: false);
            _netManager = new NetManager(this) { AutoRecycle = true, NatPunchEnabled = true };
            _netManager.DisconnectTimeout = (int)(_provider.DisconnectTimeout * 1000);
            _netManager.ReconnectDelay = (int)(_provider.ReconnectInterval * 1000);
            _netManager.MaxConnectAttempts = _provider.MaxConnectAttempts;
            _netManager.UpdateTime = (int)(_provider.UpdateInterval * 1000);
            _netManager.NatPunchModule.Init(this);

            for (int i = 0; i < Engine.Config.MaxPlayers; i++)
                _freeClients.Enqueue(new LNLNATPunchConnection(this));
        }

        private float _startNatPunchTime;

        public override void PollEvents()
        {
            _netManager.PollEvents();
            _netManager.NatPunchModule.PollEvents();
            _userNatPunchModule?.PollUpdate();

            if (Engine.IsClient)
            {
                bool isTimerRunning = _startNatPunchTime > 0f;

                if (!isTimerRunning) return;

                float timeNatPunchExpired = _startNatPunchTime + _provider.NatPunchTimeout;

                if (Time.time >= timeNatPunchExpired)
                {
                    IPEndPoint hostEndPoint = NetUtils.MakeEndPoint(_queuedConnectParameter.Address, _queuedConnectParameter.Port);

                    ConnectToHost(hostEndPoint);
                    _startNatPunchTime = 0;

                    Debug.LogWarning($"LiteNetLib: NAT Punch Timeout. Connecting directly to: {hostEndPoint}...");
                }
            }
        }

        public override void ForceUpdate() => _netManager.TriggerUpdate();

        public override void Run(RunMode mode, int port)
        {
            _port = port;

            if (mode == RunMode.Client)
            {
                _netManager.UnconnectedMessagesEnabled = true;
                _netManager.Start();
            }
            else
            {
                _netManager.BroadcastReceiveEnabled = true;
                _netManager.Start(port);

                Debug.Log($"LiteNetLib: Registering to NAT Puncher: {_provider.NatPuncherAddress}:{_provider.NatPuncherPort}");
                _userNatPunchModule = new UserNatPunchModule(_netManager, _provider.NatPuncherAddress, _provider.NatPuncherPort, _provider.NatPunchHeartbeat);
                _userNatPunchModule.RegisterToNatPunch();
                _userNatPunchModule.ResetHeartbeat();
            }
        }

        public override void Shutdown()
        {
            _netManager.Stop();
        }


        public class UserNatPunchModule
        {
            private NetManager _netManager;
            private IPEndPoint _natPunchRelayEndPoint;

            private float _lastRegisterTime;
            private float _intervalReRegister;

            public UserNatPunchModule(NetManager netManager, string natPuncherRelayAddress, int natPunchRelayPort, float intervalHeartbeat)
            {
                _netManager = netManager;
                _natPunchRelayEndPoint = new(IPAddress.Parse(natPuncherRelayAddress), natPunchRelayPort);
                _intervalReRegister = intervalHeartbeat;
            }

            public void PollUpdate()
            {
                bool isCooldownExpired = Time.time >= _lastRegisterTime + _intervalReRegister;

                if (!isCooldownExpired) return;

                Debug.Log($"LiteNetLib: Re-Registering to NAT: {_natPunchRelayEndPoint.Address}:{_natPunchRelayEndPoint.Port}");

                RegisterToNatPunch();
                ResetHeartbeat();
            }

            public void ResetHeartbeat()
            {
                _lastRegisterTime = Time.time;
            }

            public void RegisterToNatPunch()
            {
                RegisterNatPacket packet = new();

                string networkIp = NetUtils.GetLocalIp(LocalAddrType.IPv4);
                if (string.IsNullOrEmpty(networkIp))
                {
                    networkIp = NetUtils.GetLocalIp(LocalAddrType.IPv6);
                }

                packet.Internal = NetUtils.MakeEndPoint(networkIp, _netManager.LocalPort);

                NetDataWriter writer = new();
                packet.Serialize(writer);

                _netManager.SendUnconnectedMessage(writer.Data, _natPunchRelayEndPoint);
            }
        }


        public override void Connect(string address, int port, byte[] connectionData, int connectionDataLen)
        {
            _queuedConnectParameter = new ConnectParameter(address, port, connectionData, connectionDataLen);

            if (!_netManager.IsRunning)
                _netManager.Start();

            bool isLocalConnect = IsLocalhost(address);

            if (isLocalConnect)
            {
                Debug.Log($"LiteNetLib: Skipping NAT Punch because remoteEndPoint is local");
                IPEndPoint hostEndPoint = NetUtils.MakeEndPoint(_queuedConnectParameter.Address, _queuedConnectParameter.Port);

                ConnectToHost(hostEndPoint);
                return;
            }

            _startNatPunchTime = Time.time;

            string token = new IPEndPoint(IPAddress.Parse(address), port).ToString();
            Debug.Log($"LiteNetLib: Requesting NAT of: {token}...");
            _netManager.NatPunchModule.SendNatIntroduceRequest(_provider.NatPuncherAddress, _provider.NatPuncherPort, token);
        }

        private bool IsLocalhost(string address)
        {
            return address == "127.0.0.1" || address == "::1" || address.ToLower() == "localhost";
        }

        public override void Disconnect(TransportConnection connection)
        {
            _netManager.DisconnectPeer(((LNLNATPunchConnection)connection).LNLPeer);
        }

        void INetEventListener.OnConnectionRequest(ConnectionRequest request)
        {
            if (_clients.Count >= Engine.Config.MaxPlayers)
            {
                request.Reject();
                return;
            }

            int len = request.Data.AvailableBytes;
            request.Data.GetBytes(_connectionBytes, 0, len);
            bool accepted = NetworkPeer.OnConnectRequest(_connectionBytes, len, request.RemoteEndPoint.ToNetickEndPoint());

            if (accepted)
                request.Accept();
            else
                request.Reject();
        }

        void INetEventListener.OnPeerConnected(NetPeer peer)
        {
            var connection = _freeClients.Dequeue();
            connection.LNLPeer = peer;
            _clients.Add(peer, connection);
            NetworkPeer.OnConnected(connection);
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            if (!Engine.IsServer)
            {
                if (disconnectInfo.Reason == DisconnectReason.ConnectionRejected)
                {
                    NetworkPeer.OnConnectFailed(ConnectionFailedReason.Refused);
                    return;
                }

                if (disconnectInfo.Reason == DisconnectReason.ConnectionFailed || disconnectInfo.Reason == DisconnectReason.Timeout)
                {
                    NetworkPeer.OnConnectFailed(ConnectionFailedReason.Timeout);
                    return;
                }

                if (peer == null)
                {
                    Debug.Log($"LiteNetLib Network Error: {disconnectInfo.Reason}");
                    NetworkPeer.OnConnectFailed(ConnectionFailedReason.Refused);
                    return;
                }
            }

            if (peer == null)
                return;

            if (_clients.ContainsKey(peer))
            {
                TransportDisconnectReason reason = disconnectInfo.Reason == DisconnectReason.Timeout ? TransportDisconnectReason.Timeout : TransportDisconnectReason.Shutdown;
                NetworkPeer.OnDisconnected(_clients[peer], reason);
                _freeClients.Enqueue(_clients[peer]);
                _clients.Remove(peer);
            }
        }

        unsafe void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            if (!_clients.TryGetValue(peer, out var c))
                return;

            fixed (byte* ptr = reader.RawData)
            {
                _buffer.SetFrom(ptr + reader.Position, reader.AvailableBytes, reader.RawData.Length);
                NetworkPeer.Receive(c, _buffer);
            }
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError)
        {
            Debug.Log("LiteNetLib Network Error: " + socketError);
            NetworkPeer.OnConnectFailed(ConnectionFailedReason.Refused);
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

        void INatPunchListener.OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token) { }

        void INatPunchListener.OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token)
        {
            if (Engine.IsServer) return;

            Debug.Log($"LiteNetLib: NAT Bypass success! Connecting to: {targetEndPoint}");

            _startNatPunchTime = 0f;

            ConnectToHost(targetEndPoint);
        }

        private void ConnectToHost(IPEndPoint targetEndPoint)
        {
            if (Engine.IsClient)
            {
                if (_queuedConnectParameter.NullConnectionData)
                    _netManager.Connect(targetEndPoint, "");
                else
                {
                    _writer.Reset();
                    _writer.Put(_queuedConnectParameter.ConnectionData, 0, _queuedConnectParameter.ConnectionDataLen);
                    _netManager.Connect(targetEndPoint, _writer);
                }
            }
        }


        private struct ConnectParameter
        {
            public string Address;
            public int Port;
            public byte[] ConnectionData;
            public int ConnectionDataLen;

            public bool NullConnectionData => ConnectionData == null;

            public ConnectParameter(string address, int port, byte[] connectionData, int connectionDataLen)
            {
                Address = address;
                Port = port;
                ConnectionData = connectionData;
                ConnectionDataLen = connectionDataLen;
            }
        }

        private class RegisterNatPacket : INetSerializable
        {
            public IPEndPoint Internal { get; set; }

            public void Serialize(NetDataWriter writer)
            {
                writer.Put(Internal.Address.ToString()); // Serialize IP address as string
                writer.Put(Internal.Port);              // Serialize port as int
            }

            public void Deserialize(NetDataReader reader)
            {
                string ipAddress = reader.GetString();
                int port = reader.GetInt();
                Internal = new IPEndPoint(IPAddress.Parse(ipAddress), port);
            }
        }
    }
}