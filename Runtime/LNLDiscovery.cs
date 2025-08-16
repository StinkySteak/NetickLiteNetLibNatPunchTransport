using System.Net.Sockets;
using System.Net;
using LiteNetLib;
using LiteNetLib.Utils;
using UnityEngine;
using System.Collections.Generic;

namespace Netick.Transport
{
    public interface ILNLDiscoveryListener
    {
        void OnDiscoveredSessionUpdated(IReadOnlyList<DiscoveredSession> sessions);
    }

    public class LNLDiscovery
    {
        private const int DefaultSecret = 5555;

        private int _lanDiscoverySecret = DefaultSecret;
        private int _startPort;
        private int _endPort;
        private float _updateRemoveExpiredSessionInterval = 1f;
        private float _discoverInterval = 2f;
        private float _sessionLifetime = 5f;

        private float _timeRemoveExpiredSession;
        private float _timeDiscoverSession;

        private ILNLDiscoveryListener _discoveryListener;
        private NetManager _netManager;
        private ClientListener _listener;
        private NetDataWriter _writer;

        private List<DiscoveredSession> _discoveredSessions;
        private bool _isDiscoveredSessionChanged;

        private bool _isRunning;
        public bool IsRunning => _isRunning;


        public LNLDiscovery(ILNLDiscoveryListener discoveryListener, int lanDiscoverySecret, int startPort, int endPort)
        {
            _discoveryListener = discoveryListener;
            _lanDiscoverySecret = lanDiscoverySecret;
            _startPort = startPort;
            _endPort = endPort;

            _listener = new ClientListener(this, _lanDiscoverySecret);

            _netManager = new NetManager(_listener)
            {
                BroadcastReceiveEnabled = true,
                UnconnectedMessagesEnabled = true,
                IPv6Enabled = false,
                SimulateLatency = true,
                SimulationMaxLatency = 1500,
            };

            _writer = new();
            _discoveredSessions = new List<DiscoveredSession>(8);
        }

        public void Start()
        {
            _netManager.Start();
            _isRunning = true;
        }

        public void Stop()
        {
            _netManager.Stop();
            _isRunning = false;
        }

        private void DiscoverHost()
        {
            int portCount = (_endPort - _startPort) + 1;

            for (int i = 0; i < portCount; i++)
            {
                int port = _startPort + i;

                _netManager.SendBroadcast(_writer, port);
                _writer.Reset();
            }

            _timeDiscoverSession = Time.time;
        }

        public void PollUpdate()
        {
            if (!_isRunning)
            {
                return;
            }

            _netManager.PollEvents();

            if (Time.time >= _timeRemoveExpiredSession + _updateRemoveExpiredSessionInterval)
            {
                RemoveExpiredSessions();
            }

            if (Time.time >= _timeDiscoverSession + _discoverInterval)
            {
                DiscoverHost();
            }

            if (_isDiscoveredSessionChanged)
            {
                _discoveryListener.OnDiscoveredSessionUpdated(_discoveredSessions);
                _isDiscoveredSessionChanged = false;
            }
        }

        public void OnHostDiscovered(DiscoveredSession discoveredSession)
        {
            AddOrUpdate(discoveredSession);
        }
        private void RemoveExpiredSessions()
        {
            for (int i = _discoveredSessions.Count - 1; i >= 0; i--)
            {
                DiscoveredSession session = _discoveredSessions[i];

                float expireTime = session.DiscoveredTimestamp + _sessionLifetime;
                bool isExpired = Time.time >= expireTime;

                if (isExpired)
                {
                    _discoveredSessions.RemoveAt(i);
                    _isDiscoveredSessionChanged = true;
                }
            }

            _timeRemoveExpiredSession = Time.time;
        }

        private void AddOrUpdate(DiscoveredSession discoveredSession)
        {
            for (int i = 0; i < _discoveredSessions.Count; i++)
            {
                DiscoveredSession session = _discoveredSessions[i];

                bool isExisting = session.HostName == discoveredSession.HostName
                    && Equals(session.EndPoint, discoveredSession.EndPoint);

                if (isExisting)
                {
                    _discoveredSessions[i] = discoveredSession;
                    return;
                }
            }

            _discoveredSessions.Add(discoveredSession);
            _isDiscoveredSessionChanged = true;
        }

        internal class ClientListener : INetEventListener
        {
            private LNLDiscovery _discovery;
            private int _lanDiscoverySecret = 5555;

            public ClientListener(LNLDiscovery discovery, int lanDiscoverySecret)
            {
                _lanDiscoverySecret = lanDiscoverySecret;
                _discovery = discovery;
            }

            public void OnConnectionRequest(ConnectionRequest request)
            {
            }

            public void OnNetworkError(IPEndPoint endPoint, SocketError socketError)
            {
            }

            public void OnNetworkLatencyUpdate(NetPeer peer, int latency)
            {
            }

            public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod)
            {
            }

            public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
            {
                if (reader.TryGetInt(out int messageId))
                {
                    if (messageId == _lanDiscoverySecret)
                    {
                        string hostName = reader.GetString();

                        DiscoveredSession session = new();
                        session.HostName = hostName;
                        session.EndPoint = remoteEndPoint;
                        session.DiscoveredTimestamp = Time.time;

                        _discovery.OnHostDiscovered(session);
                    }
                }
            }

            public void OnPeerConnected(NetPeer peer)
            {
            }

            public void OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo)
            {
            }
        }
    }
}
