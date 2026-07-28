# V380 Decoder

Extract video and audio from encrypted V380 camera. Newer V380 cameras use encryption for video and audio streams. This project is the result of reverse engineering the V380 Pro APK to discover the decryption methods.

This is a port of [prsyahmi/v380](https://github.com/prsyahmi/v380) with significant enhancements:
- ✅ Audio and video decryption
- ✅ RTSP server output
- ✅ ONVIF support
- ✅ Web UI and REST API for camera control
- ✅ Snapshot API
- ✅ Cloud relay streaming support
- ✅ H.265/HEVC streams (`0x28` keyframes and `0x29` inter frames)
- ✅ Automatic media decryption layout detection for protocol versions 20/21

## Tested Cameras

H.264 device-version-31 cameras and encrypted H.265 device-version-32
cloud streams are supported. V380 is a protocol family rather than one fixed
camera format, so logs from additional models are welcome.

**Camera 1:**
- Software: `AppEV2W_VA3_V2.5.9.5_20231211`
- Firmware: `Hw_AWT3710D_XHR_V1.0_WF_20230519`

**Camera 2:**
- Software: `AppEV2W_VA3_V1.3.7.0_20231211`
- Firmware: `Hw_AWT3610E_XHR_E_V1.0_WF_20230607`

## Requirements

- .NET 10 SDK (for building from source)
- FFmpeg (required for H.265 snapshots/MJPEG; optional for H.264-only use)

## Command Line Arguments

| Argument | Default | Required | Description |
|----------|---------|----------|-------------|
| `--id` | - | ✅ Yes | Camera device ID |
| `--username` | `admin` | ✅ Yes | Camera username |
| `--password` | - | ✅ Yes | Camera password |
| `--ip` | - | ⚠️ If LAN | Camera IP address (required for LAN source) |
| `--port` | `8800` | No | Camera port |
| `--source` | `lan` | No | Connection source: `lan` or `cloud` |
| `--output` | `rtsp` | No | Output mode: `video`, `audio`, or `rtsp` |
| `--enable-onvif` | `false` | No | Enable ONVIF server (requires `output=rtsp`) |
| `--enable-api` | `false` | No | Enable Web UI and REST API |
| `--enable-mjpeg` | `false` | No | Enable Mjpeg stream |
| `--rtsp-port` | `8554` | No | RTSP server port |
| `--http-port` | `8080` | No | Web server port (for ONVIF/API) |
| `--secure` | `false` | No | Enable authentication for ONVIF, RTSP and API. Uses the same username and password as the V380 camera |
| `--debug` | `false` | No | Enable debug logging |
| `--discover` | `false` | No | Discover camera |
| `--help` | `false` | No | Print help |

## Usage Examples

Download Latest [Release](https://github.com/PyanSofyan/V380decoder/releases/latest)

### Find Camera Device
```bash
./V380Decoder --discover
```
### Video Output (pipe to FFplay)
```bash
# H.264 camera
./V380Decoder --id 12345678 --username admin --password password --ip 192.168.1.2 --output video | ffplay -f h264 -i pipe:0

# H.265 camera
./V380Decoder --id 12345678 --username admin --password password --source cloud --output video | ffplay -f hevc -i pipe:0
```

### Audio Output (pipe to FFplay)
```bash
./V380Decoder --id 12345678 --username admin --password password --ip 192.168.1.2 --output audio | ffplay -f alaw -ar 8000 -ac 1 -i pipe:0
```

### RTSP Server
```bash
# Default (RTSP mode)
./V380Decoder --id 12345678 --username admin --password password --ip 192.168.1.2

# Or explicitly specify
./V380Decoder --id 12345678 --username admin --password password --ip 192.168.1.2 --output rtsp
```

**Access stream:**
```
rtsp://192.168.1.3:8554/live
```

**Access snapshot:**
```
http://192.168.1.3:8080/snapshot
```

### Cloud Streaming
Streaming via relay server (relay IP automatically detected):
```bash
./V380Decoder --id 12345678 --username admin --password password --source cloud
```

For newer encrypted cameras, startup logs should report the detected codec and
decryption layout, for example:

```text
[VIDEO] codec=H265 decrypt=FullBlocks header=16
[RTSP] video codec=H265
```

If the media protocol version is absent from a relay response, the decoder
validates Annex-B NAL units and automatically chooses between the version-20
selective `64/80` AES layout and version-21 full-block AES.

## ONVIF Support

Tested with [Onvif Device Manager](https://sourceforge.net/projects/onvifdm/) (ODM), [Onvif Integration](https://www.home-assistant.io/integrations/onvif/) Home Assistant and [Shinobi](https://shinobi.video/)

**Enable ONVIF:**
```bash
./V380Decoder --id 12345678 --username admin --password password --ip 192.168.1.2 --enable-onvif
```

**Access ONVIF endpoint:**
```
http://192.168.1.3:8080/onvif/device_service
```

**Features:**
- ✅ Live video
- ✅ PTZ control (pan/tilt)
- ✅ Imaging settings (light control)
- ✅ Media profiles
- ✅ Device discovery

## Web UI & REST API

Control your camera through a web interface or REST API.

**Enable MJPEG Stream:**
```bash
./V380Decoder --id 12345678 --username admin --password password --ip 192.168.1.2 --enable-mjpeg
```

### MJPEG Endpoint
```
http://192.168.1.3:8080/mjpeg
```

**Enable API:**
```bash
./V380Decoder --id 12345678 --username admin --password password --ip 192.168.1.2 --enable-api
```

### Web UI
```
http://192.168.1.3:8080
```

### REST API Endpoints

**PTZ Control:**
```bash
curl -X POST http://192.168.1.3:8080/api/ptz/right
curl -X POST http://192.168.1.3:8080/api/ptz/left
curl -X POST http://192.168.1.3:8080/api/ptz/up
curl -X POST http://192.168.1.3:8080/api/ptz/down
```

**Light Control:**
```bash
curl -X POST http://192.168.1.3:8080/api/light/on
curl -X POST http://192.168.1.3:8080/api/light/off
curl -X POST http://192.168.1.3:8080/api/light/auto
```

**Image Mode:**
```bash
curl -X POST http://192.168.1.3:8080/api/image/color   # Day mode (color)
curl -X POST http://192.168.1.3:8080/api/image/bw      # Night mode (B&W with IR)
curl -X POST http://192.168.1.3:8080/api/image/auto    # Auto switch
curl -X POST http://192.168.1.3:8080/api/image/flip    # Flip image 180°
```

**Status:**
```bash
curl http://192.168.1.3:8080/api/status
```

## Build from Source

1. **Install .NET 10 SDK**
   - Download from: https://dotnet.microsoft.com/download/dotnet/10.0

2. **Clone repository**
   ```bash
   git clone https://github.com/PyanSofyan/V380Decoder.git
   cd V380Decoder
   ```

3. **Build**
   ```bash
   dotnet build
   ```
4. **Run with arguments**
   ```bash
   dotnet run -- --id 12345678 --username admin --password password --ip 192.168.1.2
   ```

5. **Publish (optional - for deployment)**
   ```bash
   # Linux x64
   dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -o ./publish/linux-x64
   
   # Linux ARM64 (Raspberry Pi 3+)
   dotnet publish -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -o ./publish/linux-arm64

   # Linux ARM (Raspberry Pi, etc)
   dotnet publish -c Release -r linux-arm --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -o ./publish/linux-arm

   # Windows x64
   dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=true -o ./publish/win-x64

   # Windows x86 (Built without trimming due to IL Trimmer compatibility)
   dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./publish/win-x86
   
   ```

## Run as System Service

### Linux (systemd)

Create `/etc/systemd/system/v380decoder.service`:
```ini
[Unit]
Description=V380 Camera Decoder
After=network.target

[Service]
Type=simple
User=YOUR_USERNAME
WorkingDirectory=/home/YOUR_USERNAME/v380decoder
ExecStart=/home/YOUR_USERNAME/v380decoder/V380Decoder --id 12345678 --username admin --password password --ip 192.168.1.2 --enable-onvif --enable-api
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
```

Enable and start:
```bash
sudo systemctl daemon-reload
sudo systemctl enable v380decoder
sudo systemctl start v380decoder
sudo systemctl status v380decoder
```

### Docker

Build and run:
```bash
docker build -t v380decoder .
docker run -d --restart unless-stopped --network host v380decoder --id 12345678 --username admin --password password --ip 192.168.1.2 --enable-onvif --enable-api
```

The Docker image includes FFmpeg, so H.265 snapshots and MJPEG work without an
additional package installation.

## Acknowledgements

- [prsyahmi/v380](https://github.com/prsyahmi/v380) - Original V380 reverse engineering work
- [Cyberlink Security](https://cyberlinksecurity.ie/vulnerabilities-to-exploit-a-chinese-ip-camera/) - V380 vulnerability research
- Tools used: [Wireshark](https://www.wireshark.org/), [PacketSender](https://packetsender.com/), [JADX](https://github.com/skylot/jadx), [Ghidra](https://github.com/NationalSecurityAgency/ghidra), [Frida](https://github.com/frida/frida)
