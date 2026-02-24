using Microsoft.AspNetCore.Http;

namespace V380Decoder.src
{
  public class OnvifHandler
  {
    private static Timer ptzStopTimer;
    private static readonly object ptzLock = new();
    public static string Handle(string action, string body, HttpContext ctx, V380Client camera, int httpPort, int rtspPort)
    {
      if (Contains(action, body, "GetSystemDateAndTime")) return RespGetSystemDateAndTime();
      else if (Contains(action, body, "GetDeviceInformation")) return RespGetDeviceInformation(camera);
      else if (Contains(action, body, "GetServices")) return RespGetServices(ctx);
      else if (Contains(action, body, "GetScopes")) return RespGetScopes();
      else if (Contains(action, body, "GetNetworkInterfaces")) return RespGetNetworkInterfaces();
      else if (Contains(action, body, "GetDNS")) return RespGetDNS();
      else if (Contains(action, body, "GetNTP")) return RespGetNTP();
      else if (Contains(action, body, "GetHostname")) return RespGetHostname();
      else if (Contains(action, body, "GetCapabilities")) return RespGetCapabilities(ctx);
      else if (Contains(action, body, "GetProfiles")) return RespGetProfiles();
      else if (Contains(action, body, "GetProfile")) return RespGetProfile();
      else if (Contains(action, body, "GetVideoSources")) return RespGetVideoSources();
      else if (Contains(action, body, "GetVideoSourceConfigurations")) return RespGetVideoSourceConfigurations();
      else if (Contains(action, body, "GetVideoSourceConfiguration")) return RespGetVideoSourceConfig();
      else if (Contains(action, body, "GetVideoEncoderConfigurationOptions")) return RespGetVideoEncoderConfigOptions();
      else if (Contains(action, body, "GetVideoEncoderConfigurations")) return RespGetVideoEncoderConfigurations();
      else if (Contains(action, body, "GetVideoEncoderConfiguration")) return RespGetVideoEncoderConfig();
      else if (Contains(action, body, "GetAudioSources")) return RespGetAudioSources();
      else if (Contains(action, body, "GetAudioSourceConfigurations")) return RespGetAudioSourceConfigurations();
      else if (Contains(action, body, "GetAudioSourceConfiguration")) return RespGetAudioSourceConfig();
      else if (Contains(action, body, "GetAudioEncoderConfigurationOptions")) return RespGetAudioEncoderConfigOptions();
      else if (Contains(action, body, "GetAudioEncoderConfigurations")) return RespGetAudioEncoderConfigurations();
      else if (Contains(action, body, "GetAudioEncoderConfiguration")) return RespGetAudioEncoderConfig();
      else if (Contains(action, body, "GetServiceCapabilities")) return RespServiceCapabilities();
      else if (Contains(action, body, "GetPresets")) return RespGetPresets();
      else if (Contains(action, body, "GetNodes")) return RespGetNodes();
      else if (Contains(action, body, "GetConfigurationOptions")) return RespGetConfigOptions();
      else if (Contains(action, body, "GetConfigurations")) return RespGetConfigurations();
      else if (Contains(action, body, "GetConfiguration")) return RespGetConfigurations();
      else if (Contains(action, body, "GetStatus")) return RespGetStatus();
      else if (Contains(action, body, "ContinuousMove")) return HandleContinuousMove(body, camera);
      else if (Contains(action, body, "AbsoluteMove")) return SoapOk("AbsoluteMove");
      else if (Contains(action, body, "RelativeMove")) return SoapOk("RelativeMove");
      else if (Contains(action, body, "GotoHomePosition")) return SoapOk("GotoHomePosition");
      else if (Contains(action, body, "SetPreset")) return SoapOk("SetPreset");
      else if (Contains(action, body, "RemovePreset")) return SoapOk("RemovePreset");
      else if (Contains(action, body, "Stop")) return HandleStop(camera);
      else if (Contains(action, body, "SetImagingSettings")) return HandleSetImaging(body, camera);
      else if (Contains(action, body, "GetImagingSettings")) return RespGetImagingSettings();
      else if (Contains(action, body, "GetMoveOptions")) return RespGetMoveOptions();
      else if (Contains(action, body, "GetOptions")) return RespGetImagingOptions();
      else if (Contains(action, body, "GetStreamUri")) return RespGetStreamUri(ctx, rtspPort);
      else if (Contains(action, body, "GetSnapshotUri")) return RespGetSnapshotUri(ctx, httpPort);
      else
      {
        LogUtils.debug($"[ONVIF] !! Unhandled: {action}");
        return SoapFault("ActionNotSupported", action);
      }
    }

    private static string HandleContinuousMove(string body, V380Client camera)
    {
      float x = ParseFloat(body, "x");
      float y = ParseFloat(body, "y");
      float durationSec = ParseFloat(body, "Timeout", 1.0f);
      int durationMs = Math.Clamp((int)(durationSec * 1000), 100, 5000);

      lock (ptzLock)
      {
        ptzStopTimer?.Dispose();

        if (x > 0.1f) { camera.PtzRight(); LogUtils.debug("[ONVIF] PTZ → RIGHT"); }
        else if (x < -0.1f) { camera.PtzLeft(); LogUtils.debug("[ONVIF] PTZ → LEFT"); }
        else if (y > 0.1f) { camera.PtzUp(); LogUtils.debug("[ONVIF] PTZ → UP"); }
        else if (y < -0.1f) { camera.PtzDown(); LogUtils.debug("[ONVIF] PTZ → DOWN"); }

        ptzStopTimer = new Timer(_ => camera.PtzStop(), null, durationMs, Timeout.Infinite);
      }

      return SoapOk("ContinuousMove");
    }

    private static string HandleStop(V380Client camera)
    {
      lock (ptzLock)
      {
        ptzStopTimer?.Dispose();
        camera.PtzStop();
      }
      LogUtils.debug("[ONVIF] PTZ → STOP");
      return SoapOk("Stop");
    }

    private static string HandleSetImaging(string body, V380Client camera)
    {
      string ir = ParseTag(body, "IrCutFilter").ToUpper();
      switch (ir)
      {
        case "ON": camera.LightOn(); LogUtils.debug("[ONVIF] Light → ON"); break;
        case "OFF": camera.LightOff(); LogUtils.debug("[ONVIF] Light → OFF"); break;
        default: camera.LightAuto(); LogUtils.debug("[ONVIF] Light → AUTO"); break;
      }
      return SoapOk("SetImagingSettings");
    }

    private static string RespGetSystemDateAndTime()
    {
      var now = DateTime.UtcNow;
      return Envelope($@"
              <tds:GetSystemDateAndTimeResponse>
                <tds:SystemDateAndTime>
                  <tt:DateTimeType>NTP</tt:DateTimeType>
                  <tt:DaylightSavings>false</tt:DaylightSavings>
                  <tt:TimeZone><tt:TZ>UTC</tt:TZ></tt:TimeZone>
                  <tt:UTCDateTime>
                    <tt:Time>
                      <tt:Hour>{now.Hour}</tt:Hour>
                      <tt:Minute>{now.Minute}</tt:Minute>
                      <tt:Second>{now.Second}</tt:Second>
                    </tt:Time>
                    <tt:Date>
                      <tt:Year>{now.Year}</tt:Year>
                      <tt:Month>{now.Month}</tt:Month>
                      <tt:Day>{now.Day}</tt:Day>
                    </tt:Date>
                  </tt:UTCDateTime>
                </tds:SystemDateAndTime>
              </tds:GetSystemDateAndTimeResponse>");
    }

    private static string RespGetDeviceInformation(V380Client camera) => Envelope($@"
              <tds:GetDeviceInformationResponse>
                <tds:Manufacturer>V380</tds:Manufacturer>
                <tds:Model>V380 Pro</tds:Model>
                <tds:FirmwareVersion>1.0.0</tds:FirmwareVersion>
                <tds:SerialNumber>{camera.GetDeviceId()}</tds:SerialNumber>
                <tds:HardwareId>{camera.GetDeviceVersion()}</tds:HardwareId>
              </tds:GetDeviceInformationResponse>");

    private static string RespGetServices(HttpContext ctx)
    {
      string host = ctx.Request.Host.ToString();
      return Envelope($@"
              <tds:GetServicesResponse>
                <tds:Service>
                  <tds:Namespace>http://www.onvif.org/ver10/device/wsdl</tds:Namespace>
                  <tds:XAddr>http://{host}/onvif/device_service</tds:XAddr>
                  <tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version>
                </tds:Service>
                <tds:Service>
                  <tds:Namespace>http://www.onvif.org/ver10/media/wsdl</tds:Namespace>
                  <tds:XAddr>http://{host}/onvif/media_service</tds:XAddr>
                  <tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version>
                </tds:Service>
                <tds:Service>
                  <tds:Namespace>http://www.onvif.org/ver10/ptz/wsdl</tds:Namespace>
                  <tds:XAddr>http://{host}/onvif/ptz_service</tds:XAddr>
                  <tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version>
                </tds:Service>
                <tds:Service>
                  <tds:Namespace>http://www.onvif.org/ver20/imaging/wsdl</tds:Namespace>
                  <tds:XAddr>http://{host}/onvif/imaging_service</tds:XAddr>
                  <tds:Version><tt:Major>2</tt:Major><tt:Minor>60</tt:Minor></tds:Version>
                </tds:Service>
              </tds:GetServicesResponse>");
    }

    private static string RespGetScopes() => Envelope(@"
              <tds:GetScopesResponse>
                <tds:Scopes>
                  <tt:ScopeDef>Fixed</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/type/video_encoder</tt:ScopeItem>
                </tds:Scopes>
                <tds:Scopes>
                  <tt:ScopeDef>Fixed</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/type/ptz</tt:ScopeItem>
                </tds:Scopes>
                <tds:Scopes>
                  <tt:ScopeDef>Fixed</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/hardware/V380Pro</tt:ScopeItem>
                </tds:Scopes>
                <tds:Scopes>
                  <tt:ScopeDef>Configurable</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/name/V380</tt:ScopeItem>
                </tds:Scopes>
                <tds:Scopes>
                  <tt:ScopeDef>Configurable</tt:ScopeDef>
                  <tt:ScopeItem>onvif://www.onvif.org/location/</tt:ScopeItem>
                </tds:Scopes>
              </tds:GetScopesResponse>");

    private static string RespGetVideoSources() => Envelope(@"
              <trt:GetVideoSourcesResponse>
                <trt:VideoSources token=""VideoSource_1"">
                  <tt:Framerate>25</tt:Framerate>
                  <tt:Resolution>
                    <tt:Width>1280</tt:Width>
                    <tt:Height>720</tt:Height>
                  </tt:Resolution>
                  <tt:Imaging>
                    <tt:Brightness>50</tt:Brightness>
                    <tt:ColorSaturation>50</tt:ColorSaturation>
                    <tt:Contrast>50</tt:Contrast>
                    <tt:Sharpness>50</tt:Sharpness>
                    <tt:IrCutFilter>AUTO</tt:IrCutFilter>
                  </tt:Imaging>
                </trt:VideoSources>
              </trt:GetVideoSourcesResponse>");

    private static string RespGetCapabilities(HttpContext ctx)
    {
      string host = ctx.Request.Host.ToString();
      return Envelope($@"
              <tds:GetCapabilitiesResponse>
                <tds:Capabilities>
                  <tt:Device>
                    <tt:XAddr>http://{host}/onvif/device_service</tt:XAddr>
                    <tt:Network>
                      <tt:IPFilter>false</tt:IPFilter>
                      <tt:ZeroConfiguration>false</tt:ZeroConfiguration>
                      <tt:IPVersion6>false</tt:IPVersion6>
                      <tt:DynDNS>false</tt:DynDNS>
                    </tt:Network>
                    <tt:System>
                      <tt:DiscoveryResolve>false</tt:DiscoveryResolve>
                      <tt:DiscoveryBye>false</tt:DiscoveryBye>
                      <tt:RemoteDiscovery>false</tt:RemoteDiscovery>
                      <tt:SystemBackup>false</tt:SystemBackup>
                      <tt:SystemLogging>false</tt:SystemLogging>
                      <tt:FirmwareUpgrade>false</tt:FirmwareUpgrade>
                    </tt:System>
                    <tt:IO><tt:InputConnectors>0</tt:InputConnectors><tt:RelayOutputs>0</tt:RelayOutputs></tt:IO>
                  </tt:Device>
                  <tt:Media>
                    <tt:XAddr>http://{host}/onvif/media_service</tt:XAddr>
                    <tt:StreamingCapabilities>
                      <tt:RTPMulticast>false</tt:RTPMulticast>
                      <tt:RTP_TCP>true</tt:RTP_TCP>
                      <tt:RTP_RTSP_TCP>true</tt:RTP_RTSP_TCP>
                    </tt:StreamingCapabilities>
                    <tt:Extension>
                      <tt:SnapshotUri>true</tt:SnapshotUri>
                    </tt:Extension>
                  </tt:Media>
                  <tt:PTZ>
                    <tt:XAddr>http://{host}/onvif/ptz_service</tt:XAddr>
                  </tt:PTZ>
                  <tt:Imaging>
                    <tt:XAddr>http://{host}/onvif/imaging_service</tt:XAddr>
                  </tt:Imaging>
                </tds:Capabilities>
              </tds:GetCapabilitiesResponse>");
    }

    private static string RespGetProfiles() => Envelope($@"
              <trt:GetProfilesResponse>
                <trt:Profiles token=""Profile_1"" fixed=""true"">
                  <tt:Name>V380 Live</tt:Name>
                  <tt:VideoSourceConfiguration token=""VideoSrcCfg_1"">
                    <tt:Name>VideoSource</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:SourceToken>VideoSource_1</tt:SourceToken>
                    <tt:Bounds x=""0"" y=""0"" width=""1280"" height=""720""/>
                  </tt:VideoSourceConfiguration>
                  <tt:VideoEncoderConfiguration token=""VideoEnc_1"">
                    <tt:Name>H264</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:Encoding>H264</tt:Encoding>
                    <tt:Resolution><tt:Width>1280</tt:Width><tt:Height>720</tt:Height></tt:Resolution>
                    <tt:RateControl>
                      <tt:FrameRateLimit>25</tt:FrameRateLimit>
                      <tt:EncodingInterval>1</tt:EncodingInterval>
                      <tt:BitrateLimit>4096</tt:BitrateLimit>
                    </tt:RateControl>
                    <tt:H264>
                      <tt:GovLength>30</tt:GovLength>
                      <tt:H264Profile>High</tt:H264Profile>
                    </tt:H264>
                    <tt:Multicast>
                      <tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address>
                      <tt:Port>0</tt:Port><tt:TTL>0</tt:TTL><tt:AutoStart>false</tt:AutoStart>
                    </tt:Multicast>
                    <tt:SessionTimeout>PT60S</tt:SessionTimeout>
                  </tt:VideoEncoderConfiguration>
                  <tt:AudioSourceConfiguration token=""AudioSrcCfg_1"">
                    <tt:Name>AudioSource</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:SourceToken>AudioSource_1</tt:SourceToken>
                  </tt:AudioSourceConfiguration>
                  <tt:AudioEncoderConfiguration token=""AudioEnc_1"">
                    <tt:Name>PCMA</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:Encoding>G711</tt:Encoding>
                    <tt:Bitrate>64</tt:Bitrate>
                    <tt:SampleRate>8</tt:SampleRate>
                    <tt:Multicast>
                      <tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address>
                      <tt:Port>0</tt:Port><tt:TTL>0</tt:TTL><tt:AutoStart>false</tt:AutoStart>
                    </tt:Multicast>
                    <tt:SessionTimeout>PT60S</tt:SessionTimeout>
                  </tt:AudioEncoderConfiguration>
                  <tt:PTZConfiguration token=""PTZConfig_1"">
                    <tt:Name>PTZ</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:NodeToken>PTZNode_1</tt:NodeToken>
                    <tt:DefaultContinuousPanTiltVelocitySpace>
                      http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace
                    </tt:DefaultContinuousPanTiltVelocitySpace>
                    <tt:DefaultPTZTimeout>PT1S</tt:DefaultPTZTimeout>
                    <tt:PanTiltLimits>
                      <tt:Range>
                        <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace</tt:URI>
                        <tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                        <tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>
                      </tt:Range>
                    </tt:PanTiltLimits>
                  </tt:PTZConfiguration>
                </trt:Profiles>
              </trt:GetProfilesResponse>");

    // GetProfile (singular) — ODM calls this with a ProfileToken, expects Profile (not Profiles)
    private static string RespGetProfile() => Envelope($@"
              <trt:GetProfileResponse>
                <trt:Profile token=""Profile_1"" fixed=""true"">
                  <tt:Name>V380 Live</tt:Name>
                  <tt:VideoSourceConfiguration token=""VideoSrcCfg_1"">
                    <tt:Name>VideoSource</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:SourceToken>VideoSource_1</tt:SourceToken>
                    <tt:Bounds x=""0"" y=""0"" width=""1280"" height=""720""/>
                  </tt:VideoSourceConfiguration>
                  <tt:VideoEncoderConfiguration token=""VideoEnc_1"">
                    <tt:Name>H264</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:Encoding>H264</tt:Encoding>
                    <tt:Resolution><tt:Width>1280</tt:Width><tt:Height>720</tt:Height></tt:Resolution>
                    <tt:RateControl>
                      <tt:FrameRateLimit>25</tt:FrameRateLimit>
                      <tt:EncodingInterval>1</tt:EncodingInterval>
                      <tt:BitrateLimit>4096</tt:BitrateLimit>
                    </tt:RateControl>
                    <tt:H264>
                      <tt:GovLength>30</tt:GovLength>
                      <tt:H264Profile>High</tt:H264Profile>
                    </tt:H264>
                    <tt:Multicast>
                      <tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address>
                      <tt:Port>0</tt:Port><tt:TTL>0</tt:TTL><tt:AutoStart>false</tt:AutoStart>
                    </tt:Multicast>
                    <tt:SessionTimeout>PT60S</tt:SessionTimeout>
                  </tt:VideoEncoderConfiguration>
                  <tt:AudioSourceConfiguration token=""AudioSrcCfg_1"">
                    <tt:Name>AudioSource</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:SourceToken>AudioSource_1</tt:SourceToken>
                  </tt:AudioSourceConfiguration>
                  <tt:AudioEncoderConfiguration token=""AudioEnc_1"">
                    <tt:Name>PCMA</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:Encoding>G711</tt:Encoding>
                    <tt:Bitrate>64</tt:Bitrate>
                    <tt:SampleRate>8</tt:SampleRate>
                    <tt:Multicast>
                      <tt:Address><tt:Type>IPv4</tt:Type><tt:IPv4Address>0.0.0.0</tt:IPv4Address></tt:Address>
                      <tt:Port>0</tt:Port><tt:TTL>0</tt:TTL><tt:AutoStart>false</tt:AutoStart>
                    </tt:Multicast>
                    <tt:SessionTimeout>PT60S</tt:SessionTimeout>
                  </tt:AudioEncoderConfiguration>
                  <tt:PTZConfiguration token=""PTZConfig_1"">
                    <tt:Name>PTZ</tt:Name>
                    <tt:UseCount>1</tt:UseCount>
                    <tt:NodeToken>PTZNode_1</tt:NodeToken>
                    <tt:DefaultContinuousPanTiltVelocitySpace>
                      http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace
                    </tt:DefaultContinuousPanTiltVelocitySpace>
                    <tt:DefaultPTZTimeout>PT1S</tt:DefaultPTZTimeout>
                    <tt:PanTiltLimits>
                      <tt:Range>
                        <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace</tt:URI>
                        <tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                        <tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>
                      </tt:Range>
                    </tt:PanTiltLimits>
                  </tt:PTZConfiguration>
                </trt:Profile>
              </trt:GetProfileResponse>");

    private static string RespServiceCapabilities() => Envelope(@"
              <tptz:GetServiceCapabilitiesResponse>
                <tptz:Capabilities EFlip=""false"" Reverse=""false""
                  GetCompatibleConfigurations=""false"" MoveStatus=""false""/>
              </tptz:GetServiceCapabilitiesResponse>");

    private static string RespGetNodes() => Envelope(@"
              <tptz:GetNodesResponse>
                <tptz:PTZNode token=""PTZNode_1"" FixedHomePosition=""false"">
                  <tt:Name>V380 PTZ</tt:Name>
                  <tt:SupportedPTZSpaces>
                    <tt:ContinuousPanTiltVelocitySpace>
                      <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace</tt:URI>
                      <tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                      <tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>
                    </tt:ContinuousPanTiltVelocitySpace>
                  </tt:SupportedPTZSpaces>
                  <tt:MaximumNumberOfPresets>0</tt:MaximumNumberOfPresets>
                  <tt:HomeSupported>false</tt:HomeSupported>
                </tptz:PTZNode>
              </tptz:GetNodesResponse>");

    private static string RespGetConfigurations() => Envelope(@"
              <tptz:GetConfigurationsResponse>
                <tptz:PTZConfiguration token=""PTZConfig_1"">
                  <tt:Name>PTZ</tt:Name>
                  <tt:NodeToken>PTZNode_1</tt:NodeToken>
                </tptz:PTZConfiguration>
              </tptz:GetConfigurationsResponse>");

    private static string RespGetConfigOptions() => Envelope(@"
              <tptz:GetConfigurationOptionsResponse>
                <tptz:PTZConfigurationOptions>
                  <tt:Spaces>
                    <tt:ContinuousPanTiltVelocitySpace>
                      <tt:URI>http://www.onvif.org/ver10/tptz/PanTiltSpaces/VelocityGenericSpace</tt:URI>
                      <tt:XRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:XRange>
                      <tt:YRange><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:YRange>
                    </tt:ContinuousPanTiltVelocitySpace>
                  </tt:Spaces>
                  <tt:PTZTimeout><tt:Min>PT0S</tt:Min><tt:Max>PT10S</tt:Max></tt:PTZTimeout>
                </tptz:PTZConfigurationOptions>
              </tptz:GetConfigurationOptionsResponse>");

    private static string RespGetImagingSettings() => Envelope(@"
              <timg:GetImagingSettingsResponse>
                <timg:ImagingSettings>
                  <tt:Brightness>50</tt:Brightness>
                  <tt:ColorSaturation>50</tt:ColorSaturation>
                  <tt:Contrast>50</tt:Contrast>
                  <tt:Sharpness>50</tt:Sharpness>
                  <tt:IrCutFilter>AUTO</tt:IrCutFilter>
                  <tt:Exposure>
                    <tt:Mode>AUTO</tt:Mode>
                  </tt:Exposure>
                  <tt:Focus>
                    <tt:AutoFocusMode>AUTO</tt:AutoFocusMode>
                  </tt:Focus>
                  <tt:WideDynamicRange>
                    <tt:Mode>OFF</tt:Mode>
                  </tt:WideDynamicRange>
                  <tt:WhiteBalance>
                    <tt:Mode>AUTO</tt:Mode>
                  </tt:WhiteBalance>
                </timg:ImagingSettings>
              </timg:GetImagingSettingsResponse>");

    private static string RespGetStreamUri(HttpContext ctx, int rtspPort)
    {
      string host = ctx.Request.Host.Host.ToString();
      return Envelope($@"
              <trt:GetStreamUriResponse>
                <trt:MediaUri>
                  <tt:Uri>rtsp://{host}:{rtspPort}/live</tt:Uri>
                  <tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>
                  <tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>
                  <tt:Timeout>PT0S</tt:Timeout>
                </trt:MediaUri>
              </trt:GetStreamUriResponse>");
    }

    private static string RespGetSnapshotUri(HttpContext ctx, int httpPort)
    {
      string host = ctx.Request.Host.Host.ToString();
      return Envelope($@"
              <trt:GetSnapshotUriResponse>
                <trt:MediaUri>
                  <tt:Uri>http://{host}:{httpPort}/live</tt:Uri>
                  <tt:InvalidAfterConnect>false</tt:InvalidAfterConnect>
                  <tt:InvalidAfterReboot>false</tt:InvalidAfterReboot>
                  <tt:Timeout>PT0S</tt:Timeout>
                </trt:MediaUri>
              </trt:GetSnapshotUriResponse>");
    }

    private static string RespGetAudioSources() => Envelope(@"
              <trt:GetAudioSourcesResponse>
                <trt:AudioSources token=""AudioSource_1"">
                  <tt:Channels>1</tt:Channels>
                </trt:AudioSources>
              </trt:GetAudioSourcesResponse>");

    private static string RespGetVideoSourceConfig() => Envelope(@"
              <trt:GetVideoSourceConfigurationResponse>
                <trt:VideoSourceConfiguration token=""VideoSrcCfg_1"">
                  <tt:Name>VideoSource</tt:Name>
                  <tt:UseCount>1</tt:UseCount>
                  <tt:SourceToken>VideoSource_1</tt:SourceToken>
                  <tt:Bounds x=""0"" y=""0"" width=""1280"" height=""720""/>
                </trt:VideoSourceConfiguration>
              </trt:GetVideoSourceConfigurationResponse>");

    private static string RespGetAudioSourceConfig() => Envelope(@"
              <trt:GetAudioSourceConfigurationResponse>
                <trt:AudioSourceConfiguration token=""AudioSrcCfg_1"">
                  <tt:Name>AudioSource</tt:Name>
                  <tt:UseCount>1</tt:UseCount>
                  <tt:SourceToken>AudioSource_1</tt:SourceToken>
                </trt:AudioSourceConfiguration>
              </trt:GetAudioSourceConfigurationResponse>");

    private static string RespGetNetworkInterfaces() => Envelope(@"
              <tds:GetNetworkInterfacesResponse>
                <tds:NetworkInterfaces token=""eth0"">
                  <tt:Enabled>true</tt:Enabled>
                  <tt:Info>
                    <tt:Name>eth0</tt:Name>
                    <tt:HwAddress>00:00:00:00:00:00</tt:HwAddress>
                    <tt:MTU>1500</tt:MTU>
                  </tt:Info>
                  <tt:IPv4>
                    <tt:Enabled>true</tt:Enabled>
                    <tt:Config>
                      <tt:DHCP>true</tt:DHCP>
                    </tt:Config>
                  </tt:IPv4>
                </tds:NetworkInterfaces>
              </tds:GetNetworkInterfacesResponse>");

    private static string RespGetDNS() => Envelope(@"
              <tds:GetDNSResponse>
                <tds:DNSInformation>
                  <tt:FromDHCP>true</tt:FromDHCP>
                </tds:DNSInformation>
              </tds:GetDNSResponse>");

    private static string RespGetNTP() => Envelope(@"
              <tds:GetNTPResponse>
                <tds:NTPInformation>
                  <tt:FromDHCP>false</tt:FromDHCP>
                </tds:NTPInformation>
              </tds:GetNTPResponse>");

    private static string RespGetHostname() => Envelope(@"
              <tds:GetHostnameResponse>
                <tds:HostnameInformation>
                  <tt:FromDHCP>false</tt:FromDHCP>
                  <tt:Name>V380Pro</tt:Name>
                </tds:HostnameInformation>
              </tds:GetHostnameResponse>");

    private static string RespGetStatus() => Envelope($@"
              <tptz:GetStatusResponse>
                <tptz:PTZStatus>
                  <tt:Position>
                    <tt:PanTilt x=""0"" y=""0""
                      space=""http://www.onvif.org/ver10/tptz/PanTiltSpaces/PositionGenericSpace""/>
                    <tt:Zoom x=""0""
                      space=""http://www.onvif.org/ver10/tptz/ZoomSpaces/PositionGenericSpace""/>
                  </tt:Position>
                  <tt:MoveStatus>
                    <tt:PanTilt>IDLE</tt:PanTilt>
                    <tt:Zoom>IDLE</tt:Zoom>
                  </tt:MoveStatus>
                  <tt:UtcTime>{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</tt:UtcTime>
                </tptz:PTZStatus>
              </tptz:GetStatusResponse>");

    private static string RespGetPresets() => Envelope(@"
              <tptz:GetPresetsResponse>
              </tptz:GetPresetsResponse>");

    private static string RespGetMoveOptions() => Envelope(@"
              <timg:GetMoveOptionsResponse>
                <timg:MoveOptions>
                  <tt:Focus>
                    <tt:Continuous><tt:Speed><tt:Min>-1</tt:Min><tt:Max>1</tt:Max></tt:Speed></tt:Continuous>
                  </tt:Focus>
                </timg:MoveOptions>
              </timg:GetMoveOptionsResponse>");

    private static string RespGetImagingOptions() => Envelope(@"
              <timg:GetOptionsResponse>
                <timg:ImagingOptions>
                  <tt:Brightness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Brightness>
                  <tt:ColorSaturation><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:ColorSaturation>
                  <tt:Contrast><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Contrast>
                  <tt:Sharpness><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:Sharpness>
                  <tt:IrCutFilterModes>ON</tt:IrCutFilterModes>
                  <tt:IrCutFilterModes>OFF</tt:IrCutFilterModes>
                  <tt:IrCutFilterModes>AUTO</tt:IrCutFilterModes>
                  <tt:WhiteBalanceModes>AUTO</tt:WhiteBalanceModes>
                  <tt:WhiteBalanceModes>AUTO</tt:WhiteBalanceModes>
                  <tt:IrCutFilterModes>AUTO</tt:IrCutFilterModes>
                </timg:ImagingOptions>
              </timg:GetOptionsResponse>");

    private static string RespGetVideoSourceConfigurations() => Envelope(@"
              <trt:GetVideoSourceConfigurationsResponse>
                <trt:Configurations token=""VideoSrcCfg_1"">
                  <tt:Name>VideoSource</tt:Name>
                  <tt:UseCount>1</tt:UseCount>
                  <tt:SourceToken>VideoSource_1</tt:SourceToken>
                  <tt:Bounds x=""0"" y=""0"" width=""1280"" height=""720""/>
                </trt:Configurations>
              </trt:GetVideoSourceConfigurationsResponse>");

    private static string RespGetVideoEncoderConfigurations() => Envelope(@"
              <trt:GetVideoEncoderConfigurationsResponse>
                <trt:Configurations token=""VideoEnc_1"">
                  <tt:Name>H264</tt:Name>
                  <tt:UseCount>1</tt:UseCount>
                  <tt:Encoding>H264</tt:Encoding>
                  <tt:Resolution><tt:Width>1280</tt:Width><tt:Height>720</tt:Height></tt:Resolution>
                  <tt:RateControl>
                    <tt:FrameRateLimit>25</tt:FrameRateLimit>
                    <tt:EncodingInterval>1</tt:EncodingInterval>
                    <tt:BitrateLimit>4096</tt:BitrateLimit>
                  </tt:RateControl>
                  <tt:H264><tt:GovLength>30</tt:GovLength><tt:H264Profile>High</tt:H264Profile></tt:H264>
                  <tt:SessionTimeout>PT60S</tt:SessionTimeout>
                </trt:Configurations>
              </trt:GetVideoEncoderConfigurationsResponse>");

    private static string RespGetVideoEncoderConfig() => Envelope(@"
              <trt:GetVideoEncoderConfigurationResponse>
                <trt:Configuration token=""VideoEnc_1"">
                  <tt:Name>H264</tt:Name>
                  <tt:UseCount>1</tt:UseCount>
                  <tt:Encoding>H264</tt:Encoding>
                  <tt:Resolution><tt:Width>1280</tt:Width><tt:Height>720</tt:Height></tt:Resolution>
                  <tt:RateControl>
                    <tt:FrameRateLimit>25</tt:FrameRateLimit>
                    <tt:EncodingInterval>1</tt:EncodingInterval>
                    <tt:BitrateLimit>4096</tt:BitrateLimit>
                  </tt:RateControl>
                  <tt:H264><tt:GovLength>30</tt:GovLength><tt:H264Profile>High</tt:H264Profile></tt:H264>
                  <tt:SessionTimeout>PT60S</tt:SessionTimeout>
                </trt:Configuration>
              </trt:GetVideoEncoderConfigurationResponse>");

    private static string RespGetAudioSourceConfigurations() => Envelope(@"
              <trt:GetAudioSourceConfigurationsResponse>
                <trt:Configurations token=""AudioSrcCfg_1"">
                  <tt:Name>AudioSource</tt:Name>
                  <tt:UseCount>1</tt:UseCount>
                  <tt:SourceToken>AudioSource_1</tt:SourceToken>
                </trt:Configurations>
              </trt:GetAudioSourceConfigurationsResponse>");

    private static string RespGetAudioEncoderConfigurations() => Envelope(@"
              <trt:GetAudioEncoderConfigurationsResponse>
                <trt:Configurations token=""AudioEnc_1"">
                  <tt:Name>G711</tt:Name>
                  <tt:UseCount>1</tt:UseCount>
                  <tt:Encoding>G711</tt:Encoding>
                  <tt:Bitrate>64</tt:Bitrate>
                  <tt:SampleRate>8</tt:SampleRate>
                  <tt:SessionTimeout>PT60S</tt:SessionTimeout>
                </trt:Configurations>
              </trt:GetAudioEncoderConfigurationsResponse>");

    private static string RespGetAudioEncoderConfig() => Envelope(@"
              <trt:GetAudioEncoderConfigurationResponse>
                <trt:Configuration token=""AudioEnc_1"">
                  <tt:Name>G711</tt:Name>
                  <tt:UseCount>1</tt:UseCount>
                  <tt:Encoding>G711</tt:Encoding>
                  <tt:Bitrate>64</tt:Bitrate>
                  <tt:SampleRate>8</tt:SampleRate>
                  <tt:SessionTimeout>PT60S</tt:SessionTimeout>
                </trt:Configuration>
              </trt:GetAudioEncoderConfigurationResponse>");

    private static string RespGetVideoEncoderConfigOptions() => Envelope(@"
              <trt:GetVideoEncoderConfigurationOptionsResponse>
                <trt:Options>
                  <tt:QualityRange><tt:Min>0</tt:Min><tt:Max>100</tt:Max></tt:QualityRange>
                  <tt:H264>
                    <tt:ResolutionsAvailable>
                      <tt:Width>1920</tt:Width><tt:Height>1080</tt:Height>
                    </tt:ResolutionsAvailable>
                    <tt:ResolutionsAvailable>
                      <tt:Width>1280</tt:Width><tt:Height>720</tt:Height>
                    </tt:ResolutionsAvailable>
                    <tt:ResolutionsAvailable>
                      <tt:Width>640</tt:Width><tt:Height>480</tt:Height>
                    </tt:ResolutionsAvailable>
                    <tt:GovLengthRange><tt:Min>1</tt:Min><tt:Max>255</tt:Max></tt:GovLengthRange>
                    <tt:FrameRateRange><tt:Min>1</tt:Min><tt:Max>30</tt:Max></tt:FrameRateRange>
                    <tt:EncodingIntervalRange><tt:Min>1</tt:Min><tt:Max>1</tt:Max></tt:EncodingIntervalRange>
                    <tt:H264ProfilesSupported>Baseline</tt:H264ProfilesSupported>
                    <tt:H264ProfilesSupported>Main</tt:H264ProfilesSupported>
                    <tt:H264ProfilesSupported>High</tt:H264ProfilesSupported>
                  </tt:H264>
                  <tt:Extension>
                    <tt:H264>
                      <tt:BitrateRange><tt:Min>128</tt:Min><tt:Max>8192</tt:Max></tt:BitrateRange>
                    </tt:H264>
                  </tt:Extension>
                </trt:Options>
              </trt:GetVideoEncoderConfigurationOptionsResponse>");

    private static string RespGetAudioEncoderConfigOptions() => Envelope(@"
              <trt:GetAudioEncoderConfigurationOptionsResponse>
                <trt:Options>
                  <tt:Options>
                    <tt:Encoding>G711</tt:Encoding>
                    <tt:BitrateList>64</tt:BitrateList>
                    <tt:SampleRateList>8</tt:SampleRateList>
                  </tt:Options>
                  <tt:Options>
                    <tt:Encoding>PCMU</tt:Encoding>
                    <tt:BitrateList>64</tt:BitrateList>
                    <tt:SampleRateList>8</tt:SampleRateList>
                  </tt:Options>
                </trt:Options>
              </trt:GetAudioEncoderConfigurationOptionsResponse>");

    private static string SoapOk(string action)
    {
      string ns = (action.Contains("Move") || action.Contains("Stop") || action.Contains("Home") || action.Contains("Preset")) ? "tptz" : "trt";
      return Envelope($"<{ns}:{action}Response/>");
    }

    private static string SoapFault(string code, string detail) => Envelope($@"
              <s:Fault>
                <s:Code><s:Value>s:{code}</s:Value></s:Code>
                <s:Reason><s:Text>{System.Security.SecurityElement.Escape(detail)}</s:Text></s:Reason>
              </s:Fault>");

    private static string Envelope(string body) =>
        $@"<?xml version=""1.0"" encoding=""UTF-8""?>
            <s:Envelope xmlns:s=""http://www.w3.org/2003/05/soap-envelope""
                        xmlns:tt=""http://www.onvif.org/ver10/schema""
                        xmlns:tds=""http://www.onvif.org/ver10/device/wsdl""
                        xmlns:trt=""http://www.onvif.org/ver10/media/wsdl""
                        xmlns:tptz=""http://www.onvif.org/ver10/ptz/wsdl""
                        xmlns:timg=""http://www.onvif.org/ver20/imaging/wsdl"">
            <s:Body>{body}
            </s:Body>
            </s:Envelope>";

    static bool Contains(string action, string body, string keyword)
    {
      bool match = action.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
       body.Contains(keyword, StringComparison.OrdinalIgnoreCase);
      if (match) LogUtils.debug($"[ONVIF] request: {keyword}");
      return match;
    }

    static float ParseFloat(string body, string tag, float def = 0f)
    {
      // match  tag="value"  or  <tag>value</tag>
      var m = System.Text.RegularExpressions.Regex.Match(
          body, $@"{tag}=""([^""]+)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      if (!m.Success)
        m = System.Text.RegularExpressions.Regex.Match(
            body, $@"<[^>]*{tag}[^>]*>([^<]+)<", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      return m.Success && float.TryParse(m.Groups[1].Value,
          System.Globalization.NumberStyles.Float,
          System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : def;
    }

    static string ParseTag(string body, string tag)
    {
      var m = System.Text.RegularExpressions.Regex.Match(
          body, $@"<[^>]*{tag}[^>]*>([^<]*)<", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
      return m.Success ? m.Groups[1].Value.Trim() : "";
    }
  }
}