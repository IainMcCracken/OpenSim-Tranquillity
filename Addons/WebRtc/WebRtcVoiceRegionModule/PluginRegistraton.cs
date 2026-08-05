using OpenSim.Framework;

namespace WebRtcVoice;

public class PluginRegistration : IPluginRegistryProvider
{
    public void RegisterPlugins(PluginRegistry registry)
    {
        registry.Register(
            "/OpenSim/RegionModules",
            new PluginDescriptor("WebRtcVoiceRegionModule", typeof(WebRtcVoice.WebRtcVoiceRegionModule), "WebRtcVoiceRegionModule", "1.0")
        );
    }
}
