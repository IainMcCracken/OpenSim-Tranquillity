using OpenSim.Framework;

namespace WebRtcVoice;

public class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        registry.Register(
            "/OpenSim/RegionModules",
            new PluginDescriptor("WebRtcVoiceServiceModule", typeof(WebRtcVoice.WebRtcVoiceServiceModule), "WebRtcVoiceServiceModule", "1.0")
        );
    }
}
