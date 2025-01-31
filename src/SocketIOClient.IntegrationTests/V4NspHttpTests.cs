﻿using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SocketIOClient.IntegrationTests
{
    [TestClass]
    public class V4NspHttpTests : HttpBaseTests
    {
        protected override string ServerUrl => V4_NSP_HTTP;
        protected override string ServerTokenUrl => V4_NSP_HTTP_TOKEN;
    }
}