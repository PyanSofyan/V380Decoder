using V380Decoder.src;

if (args.Length > 0)
{
    int id = ArgParser.GetArg(args, "--id", 0);
    int port = ArgParser.GetArg(args, "--port", 8800);
    string username = ArgParser.GetArg(args, "--username", "admin");
    string password = ArgParser.GetArg(args, "--password", "");
    string ip = ArgParser.GetArg(args, "--ip", "");
    string source = ArgParser.GetArg(args, "--source", "lan");
    string output = ArgParser.GetArg(args, "--output", "rtsp");
    bool enableOnvif = ArgParser.GetArg(args, "--enable-onvif", false);
    bool enableApi = ArgParser.GetArg(args, "--enable-api", false);
    int rtspPort = ArgParser.GetArg(args, "--rtsp-port", 8554);
    int httpPort = ArgParser.GetArg(args, "--http-port", 8080);
    bool debug = ArgParser.GetArg(args, "--debug", false);

    if (source.Equals("lan", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(ip))
    {
        Console.Error.WriteLine("Camera ip address not set");
        return;
    }
    if (id == 0)
    {
        Console.Error.WriteLine("Camera id not set");
        return;
    }
    if (string.IsNullOrEmpty(password))
    {
        Console.Error.WriteLine("Camera password not set");
        return;
    }
    if (debug)
    {
        LogUtils.enableDebug = true;
    }

    OutputMode outputMode = output switch
    {
        "audio" => OutputMode.Audio,
        "video" => OutputMode.Video,
        _ => OutputMode.Rtsp
    };

    SourceStream sourceStream = source switch
    {
        "cloud" => SourceStream.Cloud,
        _ => SourceStream.Lan
    };

    string relayIp = string.Empty;
    if (sourceStream == SourceStream.Cloud)
    {
        relayIp = await DispatchRelayServer.GetServerIPAsync(id);
        if (string.IsNullOrEmpty(relayIp))
        {
            Console.Error.WriteLine("[V380] failed to get relay server");
        }
        Console.Error.WriteLine($"[V380] using relay server {relayIp}");
    }


    bool enableWebServer = enableApi || enableOnvif;
    var client = new V380Client(
        sourceStream == SourceStream.Cloud ? relayIp : ip,
        port,
        (uint)id,
        username,
        password,
        sourceStream,
        outputMode
    );

    RtspServer rtsp = null;
    WebServer webServer = null;
    if (outputMode == OutputMode.Rtsp)
    {
        rtsp = new(rtspPort);
        rtsp.Start();

        webServer = new WebServer(httpPort, rtspPort, client, enableApi, enableOnvif);
        webServer.Start();
    }

    var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (sender, e) =>
     {
         e.Cancel = true;
         cts.Cancel();
     };

    AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
    {
        cts.Cancel();
        Thread.Sleep(2000);
    };

    try
    {
        client.Run(rtsp, cts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("[V380] Shutdown requested");
    }
    finally
    {
        Console.Error.WriteLine("[V380] Cleaning up...");
        rtsp?.Dispose();
        webServer?.Stop();
        client.Dispose();
    }

    Console.Error.WriteLine("[V380] Stopped");
}
else
{
    Console.Error.WriteLine("[V380] No arguments provided");
}
