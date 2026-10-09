using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace V380Decoder.src
{
    public class WebServer
    {
        private readonly V380Client client;
        private readonly int httpPort;
        private readonly int rtspPort;
        private readonly bool enableApi;
        private readonly bool enableOnvif;
        private readonly bool enableMjpeg;
        private readonly bool secure;
        private readonly string username;
        private readonly string password;
        private WebApplication app;
        public WebServer(
            int httpPort,
            int rtspPort,
            V380Client client,
            bool enableApi,
            bool enableOnvif,
            bool enableMjpeg,
            bool secure,
            string username,
            string password)
        {
            this.httpPort = httpPort;
            this.rtspPort = rtspPort;
            this.client = client;
            this.enableApi = enableApi;
            this.enableOnvif = enableOnvif;
            this.enableMjpeg = enableMjpeg;
            this.secure = secure;
            this.username = username;
            this.password = password;
        }

        public void Start()
        {
            string ipAddress = NetworkHelper.GetLocalIPAddress();
            string basicAuth = string.Empty;
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://*:{httpPort}");

            builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
            });

            app = builder.Build();

            RouteGroupBuilder api = app.MapGroup("/");
            if (secure)
            {
                basicAuth = $"{username}:{password}@";
                api.AddEndpointFilter(async (context, next) =>
                {
                    var http = context.HttpContext;

                    var auth = http.Request.Headers.Authorization.ToString();

                    if (string.IsNullOrEmpty(auth) ||
                        !auth.StartsWith("Basic "))
                    {
                        http.Response.StatusCode = 401;

                        http.Response.Headers.WWWAuthenticate =
                            @"Basic realm=""V380 Authentication""";

                        return Results.Empty;
                    }

                    var encoded = auth["Basic ".Length..].Trim();

                    var credential = Encoding.UTF8.GetString(
                        Convert.FromBase64String(encoded));

                    var parts = credential.Split(':', 2);

                    if (parts.Length != 2 ||
                        parts[0] != username ||
                        parts[1] != password)
                    {
                        http.Response.StatusCode = 401;
                        return Results.Empty;
                    }

                    return await next(context);
                });
            }

            Console.Error.WriteLine($"[SNAPSHOT] http://{basicAuth}{ipAddress}:{httpPort}/snapshot");
            api.MapGet("/snapshot", async (HttpContext ctx) =>
            {
                var jpeg = await client.snapshotManager.GetSnapshotAsync(timeoutMs: 5000);

                if (jpeg == null)
                    return Results.Problem("No snapshot available yet", statusCode: 503);

                ctx.Response.Headers.CacheControl = "no-cache";
                return Results.File(jpeg, "image/jpeg");
            });

            if (enableMjpeg)
            {
                Console.Error.WriteLine($"[MJPEG] http://{basicAuth}{ipAddress}:{httpPort}/mjpeg");
                api.MapGet("/mjpeg", async (HttpContext ctx, CancellationToken ct) =>
                {
                    const string boundary = "mjpegframe";
                    ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";
                    ctx.Response.Headers.CacheControl = "no-cache";

                    var ch = System.Threading.Channels.Channel.CreateBounded<byte[]>(
                        new System.Threading.Channels.BoundedChannelOptions(2)
                        {
                            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
                        });

                    using var sub = client.snapshotManager.Subscribe(jpeg => ch.Writer.TryWrite(jpeg));

                    try
                    {
                        await foreach (var jpeg in ch.Reader.ReadAllAsync(ct))
                        {
                            var header = System.Text.Encoding.ASCII.GetBytes(
                                $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");

                            await ctx.Response.Body.WriteAsync(header, ct);
                            await ctx.Response.Body.WriteAsync(jpeg, ct);
                            await ctx.Response.Body.WriteAsync("\r\n"u8.ToArray(), ct);
                            await ctx.Response.Body.FlushAsync(ct);
                        }
                    }
                    catch (OperationCanceledException) { }
                });
            }

            if (enableApi)
            {
                Console.Error.WriteLine($"[WEB] http://{basicAuth}{ipAddress}:{httpPort}");
                Console.Error.WriteLine($"[API] http://{basicAuth}{ipAddress}:{httpPort}/api/");

                api.MapGet("/", () => Results.Content(WebPage.GetHtml(enableMjpeg), "text/html"));

                api.MapPost("/api/ptz/right", () => { client.PtzRight(); LogUtils.debug("[API] PTZ Right"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/ptz/left", () => { client.PtzLeft(); LogUtils.debug("[API] PTZ Left"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/ptz/up", () => { client.PtzUp(); LogUtils.debug("[API] PTZ Up"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/ptz/down", () => { client.PtzDown(); LogUtils.debug("[API] PTZ Down"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/ptz/stop", () => { client.PtzStop(); LogUtils.debug("[API] PTZ Stop"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });

                api.MapPost("/api/light/on", () => { client.LightOn(); LogUtils.debug("[API] Light On"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/light/off", () => { client.LightOff(); LogUtils.debug("[API] Light Off"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/light/auto", () => { client.LightAuto(); LogUtils.debug("[API] Light Auto"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });

                api.MapPost("/api/image/color", () => { client.ImageColor(); LogUtils.debug("[API] Image Color"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/image/bw", () => { client.ImageBW(); LogUtils.debug("[API] Image B&W"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/image/auto", () => { client.ImageAuto(); LogUtils.debug("[API] Image Auto"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });
                api.MapPost("/api/image/flip", () => { client.ImageFlip(); LogUtils.debug("[API] Image Flip"); return Results.Ok(new StatusResponse { status = "ok", timestamp = DateTime.UtcNow }); });

                api.MapGet("/api/status", () => Results.Ok(new StatusResponse
                {
                    status = "running",
                    timestamp = DateTime.Now
                }));
            }

            if (enableOnvif)
            {
                if (secure)
                    Console.Error.WriteLine($"[ONVIF] http://{ipAddress}:{httpPort}/onvif/device_service (WS-Security enabled: {username})");
                else
                    Console.Error.WriteLine($"[ONVIF] http://{ipAddress}:{httpPort}/onvif/device_service");

                var onvifGroup = app.MapGroup("/");

                onvifGroup.MapPost("/onvif/device_service", async (HttpContext ctx) =>
                await HandleOnvif(ctx));

                onvifGroup.MapPost("/onvif/media_service", async (HttpContext ctx) =>
                    await HandleOnvif(ctx));

                onvifGroup.MapPost("/onvif/ptz_service", async (HttpContext ctx) =>
                    await HandleOnvif(ctx));

                onvifGroup.MapPost("/onvif/imaging_service", async (HttpContext ctx) =>
                    await HandleOnvif(ctx));
            }

            Task.Run(() => app.Run());
        }

        private async Task HandleOnvif(HttpContext ctx)
        {
            string body = "";
            using (var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            string soapAction = ctx.Request.Headers["SOAPAction"].ToString();
            string contentType = ctx.Request.Headers["Content-Type"].ToString();
            var ctMatch = Regex.Match(contentType, @"action=""([^""]+)""", RegexOptions.IgnoreCase);
            string rawAction = soapAction != "" ? soapAction
                             : ctMatch.Success ? ctMatch.Groups[1].Value
                             : "";

            string action = rawAction.TrimEnd('/').Split('/').Last();
            if (action.StartsWith("wsdl", StringComparison.OrdinalIgnoreCase) && action.Length > 4)
                action = action.Substring(4);


            string resp = OnvifHandler.Handle(action, body, ctx, client, httpPort, rtspPort, secure, username, password);

            LogUtils.debug($"[ONVIF] response: {(resp.Length > 300 ? resp[..300] + "..." : resp)}");

            ctx.Response.ContentType = "application/soap+xml; charset=utf-8";
            await ctx.Response.WriteAsync(resp);
        }

        public void Stop()
        {
            app?.StopAsync().Wait();
            app?.DisposeAsync().AsTask().Wait();
        }
    }
}