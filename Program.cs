using V380Decoder.src;

if (args.Length > 0)
{
    if (args.Contains("--help") || args.Contains("-h") || args.Contains("/?"))
    {
        PrintHelp();
        return;
    }

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

static void PrintHelp()
{
    Console.WriteLine(@"
╔══════════════════════════════════════════════════════════════╗
║                          V380Decoder                         ║
╚══════════════════════════════════════════════════════════════╝

DESCRIPTION:
  Decode and stream video/audio from V380 cameras. Supports local (LAN) and 
  cloud streaming, RTSP output, ONVIF, and web API control.

USAGE:
  V380Decoder [OPTIONS]

REQUIRED ARGUMENTS:
  --id <number>          Camera ID (required)
  --username <string>    Camera username (default: admin)
  --password <string>    Camera password (required)

CONNECTION OPTIONS:
  --ip <address>         Camera IP address for LAN mode
                         Example: --ip 192.168.1.100
  
  --port <number>        Camera port (default: 8800)
                         Example: --port 8800
  
  --source <mode>        Source type: 'lan' or 'cloud' (default: lan)
                         lan   - Direct connection to camera IP
                         cloud - Connection via relay server

OUTPUT OPTIONS:
  --output <type>        Output type: 'video', 'audio', or 'rtsp' (default: rtsp)
                         video - Raw H.264 video to stdout (pipe to ffplay)
                         audio - Raw G.711 audio to stdout (pipe to ffplay)
                         rtsp  - RTSP stream server (default)

  --rtsp-port <number>   RTSP server port when output=rtsp (default: 8554)
                         Example: --rtsp-port 8554

SERVER OPTIONS:
  --enable-api           Enable web API server (default: false)
                         Provides REST API and web UI for camera control
  
  --http-port <number>   Web API server port (default: 8080)
                         Example: --http-port 8080
  
  --enable-onvif         Enable ONVIF server (experimental) (default: false)
                         Works only with --output rtsp
                         Tested with Onvif Device Manager (ODM)

OTHER OPTIONS:
  --debug                Enable debug logging (default: false)
  --help                 Show this help message

EXAMPLES:

  1. RTSP streaming (LAN):
     V380Decoder --id 12345678 --username admin --password secret --ip 192.168.1.100
     
  2. Video to stdout (pipe to ffplay):
     V380Decoder --id 12345678 --username admin --password secret --ip 192.168.1.100 --output video | ffplay -f h264 -i pipe:0
     
  3. Audio to stdout:
     V380Decoder --id 12345678 --username admin --password secret --ip 192.168.1.100 --output audio | ffplay -f alaw -ar 8000 -ac 1 -i pipe:0
     
  4. Cloud streaming with web API:
     V380Decoder --id 12345678 --username admin --password secret --source cloud --enable-api
     
  5. Complete setup with ONVIF:
     V380Decoder --id 12345678 --username admin --password secret --ip 192.168.1.100 --enable-onvif --enable-api --http-port 8080

ACCESS POINTS (when API enabled):
  • Web UI:        http://localhost:8080
  • Snapshot:      http://localhost:8080/snapshot
  • RTSP stream:   rtsp://localhost:8554/live
  • ONVIF service: http://localhost:8080/onvif/device_service
  • REST API:      http://localhost:8080/api/{command}

API COMMANDS:
  PTZ Control:
    POST /api/ptz/up, /down, /left, /right
  
  Light Control:
    POST /api/light/on, /off, /auto
  
  Image Settings:
    POST /api/image/color, /bw, /auto, /flip

NOTES:
  • For cloud mode, IP address is auto-discovered from relay server
  • ONVIF is experimental and only tested with Onvif Device Manager
  • RTSP port can be accessed by any RTSP client (VLC, ffplay, etc.)

");
}