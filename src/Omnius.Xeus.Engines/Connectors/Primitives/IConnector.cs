using System.Threading;
using System.Threading.Tasks;
using Omnius.Core.Net;
using Omnius.Core.Net.Connections;

namespace Omnius.Xeus.Engines.Connectors.Primitives
{
    public interface IConnector
    {
        ValueTask<IConnection?> ConnectAsync(OmniAddress address, string serviceId, CancellationToken cancellationToken = default);

        ValueTask<ConnectorAcceptResult> AcceptAsync(string serviceId, CancellationToken cancellationToken = default);

        ValueTask<OmniAddress[]> GetListenEndpointsAsync(CancellationToken cancellationToken = default);
    }

    public readonly struct ConnectorAcceptResult
    {
        public ConnectorAcceptResult(IConnection connection, OmniAddress address)
        {
            this.Connection = connection;
            this.Address = address;
        }

        public IConnection Connection { get; }

        public OmniAddress Address { get; }
    }
}
