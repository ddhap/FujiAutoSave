using System.Net;
using System.Net.Sockets;
using System.Text;
using FujiAutoSave.Core.Models;
using Microsoft.Extensions.Logging;

namespace FujiAutoSave.Core.Tests.Client;

public sealed class FujiCameraClientTests
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FujiCameraClient> _logger;

    public FujiCameraClientTests()
    {
        _loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        _logger = _loggerFactory.CreateLogger<FujiCameraClient>();
    }

    [Fact]
    public void Dispose_ShouldNotThrow()
    {
        var client = new FujiCameraClient(_logger);
        client.StartDiscovery();

        var exception = Record.Exception(client.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void StartDiscovery_WhenAlreadyDiscovering_ShouldThrowInvalidOperationException()
    {
        using var client = new FujiCameraClient(_logger);
        client.StartDiscovery();

        var act = client.StartDiscovery;

        var ex = Assert.Throws<InvalidOperationException>(act);
        Assert.Contains("Already discovering", ex.Message);
    }

    [Fact]
    public void StartDiscovery_ThenStopDiscovery_ShouldCompleteWithoutError()
    {
        using var client = new FujiCameraClient(_logger);

        var exception = Record.Exception(() =>
        {
            client.StartDiscovery();
            client.StopDiscovery();
        });
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_WhenNotStarted_ShouldNotThrow()
    {
        var client = new FujiCameraClient(_logger);

        var exception = Record.Exception(client.Dispose);
        Assert.Null(exception);
    }

    [Fact]
    public void Dispose_MultipleTimes_ShouldNotThrow()
    {
        var client = new FujiCameraClient(_logger);
        client.Dispose();

        var exception = Record.Exception(client.Dispose);
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("FUJIFILM-X-S10-0EA7", "X-S10", "X-S10")]
    [InlineData("FUJIFILM", "\"X-T30 II\"", "X-T30 II")]
    public async Task RegisterAndConnect_WithSimulatedCamera_ShouldTriggerEvents(string cameraName, string wireModel, string expectedModel)
    {
        using var notifyListener = new TcpListener(IPAddress.Loopback, 0);
        notifyListener.Start();
        var notifyPort = ((IPEndPoint)notifyListener.LocalEndpoint).Port;
        notifyListener.Stop();

        var discoveryTcs = new TaskCompletionSource<CameraInfo>();
        var connectTcs = new TaskCompletionSource<CameraInfo>();

        using var client = new FujiCameraClient(_logger)
        {
            NotifyPort = notifyPort
        };

        client.CameraRegistered += camera => Task.FromResult(discoveryTcs.TrySetResult(camera));
        client.CameraConnecting += camera => Task.FromResult(connectTcs.TrySetResult(camera));

        var notifyTask = Task.Run(async () =>
        {
            await ListenAndRespondToNotifyAsync(notifyPort);
        }, TestContext.Current.CancellationToken);

        client.StartDiscovery();

        await Task.Delay(50, TestContext.Current.CancellationToken);

        await SendDiscoverMessageAsync(client.DiscoverPort, "*");

        await notifyTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await SendRegisterMessageAsync(client.RegisterPort, cameraName, wireModel);

        var registerMessage = await discoveryTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(registerMessage);
        Assert.Equal(cameraName, registerMessage.Name);
        Assert.Equal(expectedModel, registerMessage.Model);
        Assert.Equal(IPAddress.Loopback, registerMessage.Host);
        Assert.Equal(3, registerMessage.MaxTimeout);
        Assert.Equal("*", registerMessage.Target);

        client.StopDiscovery();

        var notifyTask2 = Task.Run(async () =>
        {
            await ListenAndRespondToNotifyAsync(notifyPort);
        }, TestContext.Current.CancellationToken);

        client.StartConnection();

        await Task.Delay(50, TestContext.Current.CancellationToken);

        await SendDiscoverMessageAsync(client.ConnectPort, Environment.MachineName);

        await notifyTask2.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        await SendImportMessageAsync(client.ConnectPort, cameraName);

        var importMessage = await connectTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotNull(importMessage);
        Assert.Equal(cameraName, importMessage.Name);
        Assert.Equal(IPAddress.Loopback, importMessage.Host);
        Assert.Equal(3, importMessage.MaxTimeout);
        Assert.Equal("/guest", importMessage.Target);

        client.StopConnection();
    }

    private static async Task SendDiscoverMessageAsync(int port, string target)
    {
        var discoverMessage = $"DISCOVER {target} HTTP/1.1\r\nMX:3\r\nHOST:127.0.0.255:\u0001\r\nDSCADDR:127.0.0.1\r\nSERVICE:PCAUTOSAVE/1.0\r\n";

        using var udpClient = new UdpClient();
        await udpClient.SendAsync(Encoding.ASCII.GetBytes(discoverMessage), new IPEndPoint(IPAddress.Loopback, port));
    }

    private static async Task SendRegisterMessageAsync(int port, string cameraName, string model)
    {
        var registerMessage = $"REGISTER * HTTP/1.1\r\nHOST:192.168.137.1:51542\r\nDSCNAME:{cameraName}\r\nDSCMODEL:{model}\r\n";

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port);

        using var stream = tcpClient.GetStream();
        stream.WriteTimeout = 5000;

        var buffer = Encoding.ASCII.GetBytes(registerMessage);
        await stream.WriteAsync(buffer);
    }

    private static async Task SendImportMessageAsync(int port, string cameraName)
    {
        var importMessage = $"IMPORT /guest HTTP/1.1\r\nHOST:192.168.137.1:51541\r\nDSCNAME:{cameraName}\r\nDSCPORT:?\r\n";

        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(IPAddress.Loopback, port);

        using var stream = tcpClient.GetStream();
        stream.WriteTimeout = 5000;

        var buffer = Encoding.ASCII.GetBytes(importMessage);
        await stream.WriteAsync(buffer);
    }

    private static async Task ListenAndRespondToNotifyAsync(int port)
    {
        using var tcpListener = new TcpListener(IPAddress.Loopback, port);
        tcpListener.Start();

        var tcpClient = await tcpListener.AcceptTcpClientAsync();
        using (tcpClient)
        {
            var stream = tcpClient.GetStream();
            var buffer = new byte[1024];
            _ = await stream.ReadAsync(buffer);

            var response = "HTTP/1.1 200 OK";
            var responseBuffer = Encoding.ASCII.GetBytes(response);
            await stream.WriteAsync(responseBuffer);
        }
    }
}
