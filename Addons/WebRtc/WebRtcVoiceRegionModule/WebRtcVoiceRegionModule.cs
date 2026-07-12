// Copyright 2024 Robert Adams (misterblue@misterblue.com)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at http://mozilla.org/MPL/2.0/.
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System.Net;
using System.Reflection;

using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;

using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;

using log4net;
using Nini.Config;

namespace WebRtcVoice
{
    /// <summary>
    /// This module provides the WebRTC voice interface for viewer clients..
    /// 
    /// In particular, it provides the following capabilities:
    ///      ProvisionVoiceAccountRequest, VoiceSignalingRequest, and ParcelVoiceInfoRequest.    
    /// which are the user interface to the voice service.
    /// 
    /// Initially, when the user connects to the region, the region feature "VoiceServiceType" is
    /// set to "webrtc" and the capabilities that support voice are enabled.
    /// The capabilities then pass the user request information to the IWebRtcVoiceService interface
    /// that has been registered for the reqion.
    /// </summary>
    public class WebRtcVoiceRegionModule : ISharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string logHeader = "[REGION WEBRTC VOICE]";

        private bool _MessageDetails = false;

        // Control info
        private static bool m_Enabled = false;

        private readonly Dictionary<string, string> m_UUIDName = new Dictionary<string, string>();
        private Dictionary<string, string> m_ParcelAddress = new Dictionary<string, string>();

        private IConfig m_Config;

        // ISharedRegionModule.Initialize
        public void Initialise(IConfigSource config)
        {
            m_Config = config.Configs["WebRtcVoice"];
            if (m_Config is not null)
            {
                m_Enabled = m_Config.GetBoolean("Enabled", false);
                if (m_Enabled)
                {
                    _MessageDetails = m_Config.GetBoolean("MessageDetails", false);

                    m_log.Info($"{logHeader}: enabled");
                }
            }
        }

        // ISharedRegionModule.PostInitialize
        public void PostInitialise()
        {
        }

        // ISharedRegionModule.AddRegion
        public void AddRegion(Scene scene)
        {
            if (m_Enabled)
            {
                // Get the hook that means Capbibilities are being registered
                scene.EventManager.OnRegisterCaps += (UUID agentID, Caps caps) =>
                    {
                        OnRegisterCaps(scene, agentID, caps);
                    };
                
            }
        }

        // ISharedRegionModule.RemoveRegion
        public void RemoveRegion(Scene scene)
        {
            var sfm = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
            sfm.OnSimulatorFeaturesRequest -= OnSimulatorFeatureRequestHandler;
        }

        // ISharedRegionModule.RegionLoaded
        public void RegionLoaded(Scene scene)
        {
            if (m_Enabled)
            {
                // Register for the region feature reporting so we can add 'webrtc'
                var sfm = scene.RequestModuleInterface<ISimulatorFeaturesModule>();
                sfm.OnSimulatorFeaturesRequest += OnSimulatorFeatureRequestHandler;
                m_log.DebugFormat("{0}: registering OnSimulatorFeatureRequestHandler", logHeader);
            }
        }

        // ISharedRegionModule.Close
        public void Close()
        {
        }

        // ISharedRegionModule.Name
        public string Name
        {
            get { return "RegionVoiceModule"; }
        }

        // ISharedRegionModule.ReplaceableInterface
        public Type ReplaceableInterface
        {
            get { return null; }
        }

        // Called when the simulator features are being constructed.
        // Add the flag that says we support WebRtc voice.
        private void OnSimulatorFeatureRequestHandler(UUID agentID, ref OSDMap features)
        {
            m_log.DebugFormat("{0}: setting VoiceServerType=webrtc for agent {1}", logHeader, agentID);
            features["VoiceServerType"] = "webrtc";
        }

        // <summary>
        // OnRegisterCaps is invoked via the scene.EventManager
        // everytime OpenSim hands out capabilities to a client
        // (login, region crossing). We contribute three capabilities to
        // the set of capabilities handed back to the client:
        // ProvisionVoiceAccountRequest, VoiceSignalingRequest, and ParcelVoiceInfoRequest.
        //
        // ProvisionVoiceAccountRequest allows the client to obtain
        // voice communication information the the avater.
        //
        // VoiceSignalingRequest: Used for trickling ICE candidates.
        //
        // ParcelVoiceInfoRequest is invoked whenever the client
        // changes from one region or parcel to another.
        //
        // Note that OnRegisterCaps is called here via a closure
        // delegate containing the scene of the respective region (see
        // Initialise()).
        // </summary>
        public void OnRegisterCaps(Scene scene, UUID agentID, Caps caps)
        {
            m_log.DebugFormat(
                "{0}: OnRegisterCaps() called with agentID {1} caps {2} in scene {3}",
                logHeader, agentID, caps, scene.RegionInfo.RegionName);

            caps.RegisterSimpleHandler("ProvisionVoiceAccountRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        ProvisionVoiceAccountRequest(httpRequest, httpResponse, agentID, scene);
                    }));

            caps.RegisterSimpleHandler("VoiceSignalingRequest",
                    new SimpleStreamHandler("/" + UUID.Random(), (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse) =>
                    {
                        VoiceSignalingRequest(httpRequest, httpResponse, agentID, scene);
                    }));
        }

        /// <summary>
        /// Callback for a client request for Voice Account Details
        /// </summary>
        /// <param name="scene">current scene object of the client</param>
        /// <param name="request"></param>
        /// <param name="path"></param>
        /// <param name="param"></param>
        /// <param name="agentID"></param>
        /// <param name="caps"></param>
        /// <returns></returns>
        public void ProvisionVoiceAccountRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if(request.HttpMethod != "POST")
            {
                m_log.DebugFormat("[{0}][ProvisionVoice]: Not a POST request. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // Deserialize the request. Convert the LLSDXml to OSD for our use
            OSDMap map = null;
            using (Stream inputStream = request.InputStream)
            {
                if (inputStream.Length > 0)
                {
                    OSD tmp = OSDParser.DeserializeLLSDXml(inputStream);
                    if (_MessageDetails) m_log.DebugFormat("{0}[ProvisionVoice]: Request: {1}", logHeader, tmp.ToString());
                    map = tmp as OSDMap;
                }
            }

            if (map is null)
            {
                m_log.ErrorFormat("{0}[ProvisionVoice]: No request data found. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            // Get the voice service. If it doesn't exist, return an error.
            IWebRtcVoiceService voiceService = scene.RequestModuleInterface<IWebRtcVoiceService>();
            if (voiceService is null)
            {
                m_log.ErrorFormat("{0}[ProvisionVoice]: avatar \"{1}\": no voice service", logHeader, agentID);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // Make sure the request is for WebRtc voice
            if (map.TryGetValue("voice_server_type", out OSD vstosd))
            {
                if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
                {
                    m_log.WarnFormat("{0}[ProvisionVoice]: voice_server_type is not 'webrtc'. Request: {1}", logHeader, map.ToString());
                    response.StatusCode = (int)HttpStatusCode.NotFound;
                    // response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }
            }

            // The checks passed. Send the request to the voice service.
            OSDMap resp = voiceService.ProvisionVoiceAccountRequest(map, agentID, scene.RegionInfo.RegionID).Result;

            if (_MessageDetails) m_log.DebugFormat("{0}[ProvisionVoice]: response: {1}", logHeader, resp.ToString());

            // TODO: check for errors and package the response

            // Convert the OSD to LLSDXml for the response
            string xmlResp = OSDParser.SerializeLLSDXmlString(resp);

            response.StatusCode = (int)HttpStatusCode.OK;
            response.RawBuffer = Util.UTF8.GetBytes(xmlResp);
            return;
        }

        public void VoiceSignalingRequest(IOSHttpRequest request, IOSHttpResponse response, UUID agentID, Scene scene)
        {
            if(request.HttpMethod != "POST")
            {
                m_log.ErrorFormat("[{0}][VoiceSignaling]: Not a POST request. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            // Deserialize the request. Convert the LLSDXml to OSD for our use
            OSDMap map = null;
            using (Stream inputStream = request.InputStream)
            {
                if (inputStream.Length > 0)
                {
                    OSD tmp = OSDParser.DeserializeLLSDXml(inputStream);
                    if (_MessageDetails) m_log.DebugFormat("{0}[VoiceSignalingRequest]: Request: {1}", logHeader, tmp.ToString());

                    if (tmp is OSDMap)
                    {
                        map = (OSDMap)tmp;
                    }
                }
            }
            if (map is null)
            {
                m_log.ErrorFormat("{0}[VoiceSignalingRequest]: No request data found. Agent={1}", logHeader, agentID.ToString());
                response.StatusCode = (int)HttpStatusCode.NoContent;
                return;
            }

            // Make sure the request is for WebRTC voice
            if (map.TryGetValue("voice_server_type", out OSD vstosd))
            {
                if (vstosd is OSDString vst && !((string)vst).Equals("webrtc", StringComparison.OrdinalIgnoreCase))
                {
                    response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
                    return;
                }
            }

            IWebRtcVoiceService voiceService = scene.RequestModuleInterface<IWebRtcVoiceService>();
            if (voiceService is null)
            {
                m_log.ErrorFormat("{0}[VoiceSignalingRequest]: avatar \"{1}\": no voice service", logHeader, agentID);
                response.StatusCode = (int)HttpStatusCode.NotFound;
                return;
            }

            OSDMap resp = voiceService.VoiceSignalingRequest(map, agentID, scene.RegionInfo.RegionID).Result;
            if (_MessageDetails) m_log.DebugFormat("{0}[VoiceSignalingRequest]: Response: {1}", logHeader, resp);

            // TODO: check for errors and package the response

            response.StatusCode = (int)HttpStatusCode.OK;
            response.RawBuffer = Util.UTF8.GetBytes("<llsd><undef /></llsd>");
            return;
        }
    }
}
