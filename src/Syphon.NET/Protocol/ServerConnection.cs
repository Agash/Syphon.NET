namespace Syphon.NET.Protocol;

// A server's side of the messaging (SyphonServerConnectionManager.m). The server's port is named
// after its UUID. A client registers for info (surface changes, name changes, retirement) and for
// frames (a message per published frame); the server keeps a sender to each client's port.
internal sealed class ServerConnection : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, MessageSender> _infoClients =
    [
        with(StringComparer.Ordinal),
    ];
    private readonly Dictionary<string, MessageSender> _frameClients =
    [
        with(StringComparer.Ordinal),
    ];
    private readonly MessageReceiver _receiver;
    private uint _surfaceId;
    private bool _alive = true;

    public ServerConnection(string uuid)
    {
        _receiver =
            MessageReceiver.TryCreate(uuid, OnMessage)
            ?? throw new SyphonException($"The server's message port {uuid} could not be created.");
    }

    // Raised on the messaging queue when the first client arrives or the last one leaves.
    public event Action<bool>? ClientsChanged;

    public bool HasClients
    {
        get
        {
            lock (_gate)
            {
                return _infoClients.Count > 0;
            }
        }
    }

    public void SetSurface(uint surfaceId)
    {
        lock (_gate)
        {
            _surfaceId = surfaceId;
            foreach (MessageSender client in _infoClients.Values)
            {
                client.Send((uint)ClientMessage.UpdateSurfaceId, NSNumber.FromUInt32(surfaceId));
            }
        }
    }

    public void PublishNewFrame()
    {
        lock (_gate)
        {
            foreach (MessageSender client in _frameClients.Values)
            {
                client.Send((uint)ClientMessage.NewFrame, null);
            }
        }
    }

    public void SetName(string name)
    {
        lock (_gate)
        {
            foreach (MessageSender client in _infoClients.Values)
            {
                client.Send((uint)ClientMessage.UpdateServerName, new NSString(name));
            }
        }
    }

    // Tells every client the server is gone, and stops receiving.
    public void Dispose()
    {
        bool hadClients;
        lock (_gate)
        {
            if (!_alive)
            {
                return;
            }

            _alive = false;
            hadClients = _infoClients.Count > 0;
            foreach (MessageSender client in _infoClients.Values)
            {
                client.SendAndFlush((uint)ClientMessage.RetireServer, null);
            }

            foreach (MessageSender client in _infoClients.Values.Union(_frameClients.Values))
            {
                client.Dispose();
            }

            _infoClients.Clear();
            _frameClients.Clear();
        }

        _receiver.Dispose();
        if (hadClients)
        {
            ClientsChanged?.Invoke(false);
        }
    }

    private void OnMessage(uint type, NSObject? payload)
    {
        if (payload is not NSString client)
        {
            return;
        }

        switch ((ServerMessage)type)
        {
            case ServerMessage.AddClientForInfo:
                AddInfoClient(client);
                break;
            case ServerMessage.RemoveClientForInfo:
                RemoveInfoClient(client);
                break;
            case ServerMessage.AddClientForFrames:
                AddFrameClient(client);
                break;
            case ServerMessage.RemoveClientForFrames:
                RemoveFrameClient(client);
                break;
            default:
                break;
        }
    }

    private void AddInfoClient(string client)
    {
        bool first;
        lock (_gate)
        {
            if (!_alive || _infoClients.ContainsKey(client))
            {
                return;
            }

            MessageSender? sender = MessageSender.TryCreate(client, PruneDeadClients);
            if (sender is null)
            {
                return;
            }

            if (_surfaceId != 0)
            {
                sender.Send((uint)ClientMessage.UpdateSurfaceId, NSNumber.FromUInt32(_surfaceId));
            }

            first = _infoClients.Count == 0;
            _infoClients[client] = sender;
        }

        if (first)
        {
            ClientsChanged?.Invoke(true);
        }
    }

    private void RemoveInfoClient(string client)
    {
        bool last;
        lock (_gate)
        {
            if (!_alive || !_infoClients.Remove(client, out MessageSender? sender))
            {
                return;
            }

            if (!_frameClients.ContainsKey(client))
            {
                sender.Dispose();
            }

            last = _infoClients.Count == 0;
        }

        if (last)
        {
            ClientsChanged?.Invoke(false);
        }
    }

    private void AddFrameClient(string client)
    {
        lock (_gate)
        {
            if (!_alive)
            {
                return;
            }

            // The SDK reuses the info client's sender; a frame client without one gets its own.
            MessageSender? sender =
                _infoClients.GetValueOrDefault(client)
                ?? MessageSender.TryCreate(client, PruneDeadClients);
            if (sender is null)
            {
                return;
            }

            _frameClients[client] = sender;
            if (_surfaceId != 0)
            {
                sender.Send((uint)ClientMessage.NewFrame, null);
            }
        }
    }

    private void RemoveFrameClient(string client)
    {
        lock (_gate)
        {
            if (
                _alive
                && _frameClients.Remove(client, out MessageSender? sender)
                && !_infoClients.ContainsKey(client)
            )
            {
                sender.Dispose();
            }
        }
    }

    // A send found a client's port gone: the client died without unregistering.
    private void PruneDeadClients()
    {
        string[] dead;
        lock (_gate)
        {
            dead =
            [
                .. _infoClients.Where(static c => !c.Value.IsValid).Select(static c => c.Key),
                .. _frameClients.Where(static c => !c.Value.IsValid).Select(static c => c.Key),
            ];
        }

        foreach (string client in dead.Distinct())
        {
            RemoveFrameClient(client);
            RemoveInfoClient(client);
        }
    }
}
