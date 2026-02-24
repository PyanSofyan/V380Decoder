using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
        private WebApplication app;
        public WebServer(int httpPort, int rtspPort, V380Client client, bool enableApi, bool enableOnvif)
        {
            this.httpPort = httpPort;
            this.rtspPort = rtspPort;
            this.client = client;
            this.enableApi = enableApi;
            this.enableOnvif = enableOnvif;
        }

        public void Start()
        {
            string ipAddress = NetworkHelper.GetLocalIPAddress();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://*:{httpPort}");

            builder.Logging.ClearProviders();
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
            });

            app = builder.Build();

            Console.Error.WriteLine($"[SNAPSHOT] http://{ipAddress}:{httpPort}/snapshot");
            app.MapGet("/snapshot", (HttpContext ctx) =>
            {
                var jpeg = client.snapshotManager.GetSnapshot(timeoutMs: 5000);

                if (jpeg == null || jpeg.Length == 0)
                {
                    return Results.Problem(
                        "No snapshot available. Ensure stream is running",
                        statusCode: 503
                    );
                }

                ctx.Response.Headers["Cache-Control"] = "no-cache";

                return Results.File(jpeg, "image/jpeg");
            });

            if (enableApi)
            {
                Console.Error.WriteLine($"[WEB] http://{ipAddress}:{httpPort}");
                Console.Error.WriteLine($"[API] http://{ipAddress}:{httpPort}/api/");

                app.MapGet("/", () => Results.Content(WebPage.GetHtml(), "text/html"));

                app.MapPost("/api/ptz/right", () => { client.PtzRight(); LogUtils.debug("[API] PTZ Right"); Results.Ok(); });
                app.MapPost("/api/ptz/left", () => { client.PtzLeft(); LogUtils.debug("[API] PTZ Left"); Results.Ok(); });
                app.MapPost("/api/ptz/up", () => { client.PtzUp(); LogUtils.debug("[API] PTZ Up"); Results.Ok(); });
                app.MapPost("/api/ptz/down", () => { client.PtzDown(); LogUtils.debug("[API] PTZ Down"); Results.Ok(); });
                app.MapPost("/api/ptz/stop", () => { client.PtzStop(); LogUtils.debug("[API] PTZ Stop"); Results.Ok(); });

                app.MapPost("/api/light/on", () => { client.LightOn(); LogUtils.debug("[API] Light On"); Results.Ok(); });
                app.MapPost("/api/light/off", () => { client.LightOff(); LogUtils.debug("[API] Light Off"); Results.Ok(); });
                app.MapPost("/api/light/auto", () => { client.LightAuto(); LogUtils.debug("[API] Light Auto"); Results.Ok(); });

                app.MapPost("/api/image/color", () => { client.ImageColor(); LogUtils.debug("[API] Image Color"); Results.Ok(); });
                app.MapPost("/api/image/bw", () => { client.ImageBW(); LogUtils.debug("[API] Image B&W"); Results.Ok(); });
                app.MapPost("/api/image/auto", () => { client.ImageAuto(); LogUtils.debug("[API] Image Auto"); Results.Ok(); });
                app.MapPost("/api/image/flip", () => { client.ImageFlip(); LogUtils.debug("[API] Image Flip"); Results.Ok(); });

                app.MapGet("/api/status", () => Results.Ok(new StatusResponse
                {
                    status = "running",
                    timestamp = DateTime.Now
                }));
            }

            if (enableOnvif)
            {
                Console.Error.WriteLine($"[ONVIF] http://{ipAddress}:{httpPort}/onvif/device_service");

                app.MapPost("/onvif/device_service", async (HttpContext ctx) =>
                await HandleOnvif(ctx));

                app.MapPost("/onvif/media_service", async (HttpContext ctx) =>
                    await HandleOnvif(ctx));

                app.MapPost("/onvif/ptz_service", async (HttpContext ctx) =>
                    await HandleOnvif(ctx));

                app.MapPost("/onvif/imaging_service", async (HttpContext ctx) =>
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


            string resp = OnvifHandler.Handle(action, body, ctx, client, httpPort, rtspPort);

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