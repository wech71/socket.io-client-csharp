﻿using Microsoft.Extensions.Logging;

namespace SocketIOClient.IntegrationTests
{
    public abstract class WebSocketBaseTests : SocketIOBaseTests
    {
        protected override SocketIOOptions CreateOptions()
        {
            return new SocketIOOptions
            {
                AutoUpgrade = false
            };
        }

        protected override SocketIO CreateSocketIO()
        {
            return CreateSocketIO(new SocketIOOptions
            {
                Reconnection = false
            });
        }
    }
}