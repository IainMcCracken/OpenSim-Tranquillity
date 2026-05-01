/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System.Reflection;
using System.Timers;

using log4net;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Server.Base;
using OpenMetaverse;
using SkiaSharp;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.MapImage
{
    public class MapImageServiceModule : IMapImageUploadModule, ISharedRegionModule
    {
        private static readonly ILog m_log =
            LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static string LogHeader = "[MAP IMAGE SERVICE MODULE]:";

        private bool m_enabled = false;
        private IMapImageService m_MapService;

        private Dictionary<UUID, Scene> m_scenes = new Dictionary<UUID, Scene>();

        private int m_refreshtime = 0;
        private int m_lastrefresh = 0;
        private System.Timers.Timer m_refreshTimer;

        #region ISharedRegionModule

        public Type ReplaceableInterface { get { return null; } }
        public string Name { get { return "MapImageServiceModule"; } }
        public void RegionLoaded(Scene scene) { }
        public void Close() { }
        public void PostInitialise() { }

        ///<summary>
        ///
        ///</summary>
        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("MapImageService", "");
                if (name != Name)
                    return;
            }

            IConfig config = source.Configs["MapImageService"];
            if (config == null)
                return;

            int refreshminutes = Convert.ToInt32(config.GetString("RefreshTime"));
            if (refreshminutes < 0)
            {
                m_log.WarnFormat("[MAP IMAGE SERVICE MODULE]: Negative refresh time given in config. Module disabled.");
                return;
            }

            string service = config.GetString("LocalServiceModule", string.Empty);
            if (service.Length == 0)
            {
                m_log.WarnFormat("[MAP IMAGE SERVICE MODULE]: No service dll given in config. Unable to proceed.");
                return;
            }

            Object[] args = new Object[] { source };
            m_MapService = ServerUtils.LoadPlugin<IMapImageService>(service, args);
            if (m_MapService == null)
            {
                m_log.WarnFormat("[MAP IMAGE SERVICE MODULE]: Unable to load LocalServiceModule from {0}. MapService module disabled. Please fix the configuration.", service);
                return;
            }

            // we don't want the timer if the interval is zero, but we still want this module enables
            if(refreshminutes > 0)
            {
                m_refreshtime = refreshminutes * 60 * 1000; // convert from minutes to ms

                m_refreshTimer = new System.Timers.Timer();
                m_refreshTimer.Enabled = true;
                m_refreshTimer.AutoReset = true;
                m_refreshTimer.Interval = m_refreshtime;
                m_refreshTimer.Elapsed += new ElapsedEventHandler(HandleMaptileRefresh);


                m_log.InfoFormat("[MAP IMAGE SERVICE MODULE]: enabled with refresh time {0} min and service object {1}",
                             refreshminutes, service);
            }
            else
            {
                m_log.InfoFormat("[MAP IMAGE SERVICE MODULE]: enabled with no refresh and service object {0}", service);
            }
            m_enabled = true;
        }

        ///<summary>
        ///
        ///</summary>
        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            // Every shared region module has to maintain an indepedent list of
            // currently running regions
            lock (m_scenes)
                m_scenes[scene.RegionInfo.RegionID] = scene;

            // v2 Map generation on startup is now handled by scene to allow bmp to be shared with
            // v1 service and not generate map tiles twice as was previous behavior
            //scene.EventManager.OnRegionReadyStatusChange += s => { if (s.Ready) UploadMapTile(s); };

            scene.RegisterModuleInterface<IMapImageUploadModule>(this);
        }

        ///<summary>
        ///
        ///</summary>
        public void RemoveRegion(Scene scene)
        {
            if (! m_enabled)
                return;

            lock (m_scenes)
                m_scenes.Remove(scene.RegionInfo.RegionID);
        }

        #endregion ISharedRegionModule

        ///<summary>
        ///
        ///</summary>
        private void HandleMaptileRefresh(object sender, EventArgs ea)
        {
            // this approach is a bit convoluted becase we want to wait for the
            // first upload to happen on startup but after all the objects are
            // loaded and initialized
            if (m_lastrefresh > 0 && Util.EnvironmentTickCountSubtract(m_lastrefresh) < m_refreshtime)
                return;

            m_log.DebugFormat("[MAP IMAGE SERVICE MODULE]: map refresh!");
            lock (m_scenes)
            {
                foreach (IScene scene in m_scenes.Values)
                {
                    try
                    {
                        UploadMapTile(scene);
                    }
                    catch (Exception ex)
                    {
                        m_log.WarnFormat("[MAP IMAGE SERVICE MODULE]: something bad happened {0}", ex.Message);
                    }
                }
            }

            m_lastrefresh = Util.EnvironmentTickCount();
        }

        ///<summary>
        /// Upload map tile using SKBitmap
        ///</summary>
        public void UploadMapTile(IScene scene)
        {
            m_log.DebugFormat("{0}: upload maptile for {1}", LogHeader, scene.RegionInfo.RegionName);

            // Create a JPG map tile and upload it to the AddMapTile API
            IMapImageGenerator tileGenerator = scene.RequestModuleInterface<IMapImageGenerator>();
            if (tileGenerator == null)
            {
                m_log.WarnFormat("{0} Cannot upload map tile without an ImageGenerator", LogHeader);
                return;
            }

            using SKBitmap mapTile = tileGenerator.CreateMapTile();

            if (mapTile == null)
                return;

            // Help out the code below, especially varregions with the use of ExtractSubset.
            mapTile.SetImmutable();

            UploadMapTile(scene, mapTile);
        }

        /// <summary>
        /// IMapImageUploadModule implementation for SKBitmap map tiles.
        /// </summary>
        /// <param name="scene"></param>
        /// <param name="mapTile"></param>
        public void UploadMapTile(IScene scene, SKBitmap mapTile)
        {
            if (mapTile == null)
            {
                m_log.WarnFormat("{0} Cannot upload null image", LogHeader);
                return;
            }

            // If the region/maptile is legacy sized, just upload the one tile like it has always been done
            if (mapTile.Width == Constants.RegionSize && mapTile.Height == Constants.RegionSize)
            {
                m_log.DebugFormat("{0} Upload maptile for {1}", LogHeader, scene.Name);
                ConvertAndUploadMaptile(scene, mapTile,
                                        scene.RegionInfo.RegionLocX, scene.RegionInfo.RegionLocY,
                                        scene.RegionInfo.RegionName);
            }
            else
            {
                // For varregions, we need to divide the full sized region image into 256x256 map tiles.
                long sizeX = mapTile.Width / Constants.RegionSize;
                long sizeY = mapTile.Height / Constants.RegionSize;

                m_log.Debug($"{LogHeader}: Upload {sizeX * sizeY} maptiles for {scene.Name}");

                SKBitmap tile = new();

                for (long tileX = 0; tileX < sizeX; tileX++)
                {
                    for (long tileY = 0; tileY < sizeY; tileY++)
                    {
                        int left = (int)(tileX * Constants.RegionSize);
                        int right = (int)(left + Constants.RegionSize - 1);
                        int top = (int)(tileY * Constants.RegionSize);
                        int bottom = (int)(top + Constants.RegionSize - 1);

                        // Remember, the bitmap uses graphic coordinates where Y increases down, but the grid map uses regular
                        // math coordinates, where Y increases up.
                        uint gridX = (uint)(scene.RegionInfo.RegionLocX + tileX);
                        uint gridY = (uint)(scene.RegionInfo.RegionLocY - sizeY + tileY + 1);

                        SKRectI tileRect = new(left, top, right, bottom);
                        if (!mapTile.ExtractSubset(tile, tileRect))
                        {
                            m_log.Warn($"{LogHeader}: Failed to extract sub-tile at {left},{top}");
                            continue;
                        }

                        if (!ConvertAndUploadMaptile(scene, tile, gridX, gridY, scene.Name))
                        {
                            m_log.Debug($"{LogHeader}: Upload maptiles for {scene.Name} aborted");
                            return;
                        }
                    }
                }
            }
        }

        // New SKBitmap-based upload path using SkiaSharp for JPEG encoding.
        private bool ConvertAndUploadMaptile(IScene scene, SKBitmap tileImage, uint locX, uint locY, string regionName)
        {
            // Convert to JPEG (use 100% here, the map image service will be outoutting it at 80% to the maptiles directory).
            if (!SkiaImageUtils.TryEncodeToJpeg(tileImage, 100, out byte[] jpgData))
            {
                m_log.Warn($"{LogHeader}: Tile encode to JPEG failed for region {regionName}");
                return false;
            }

            // Upload the image.
            if (!m_MapService.AddMapTile((int)locX, (int)locY, jpgData, scene.RegionInfo.ScopeID, out string reason))
            {
                m_log.Debug($"{LogHeader}: Upload of tile for {regionName} at {locX},{locY} failed.");
                return false;
            }

            return true;
        }
    }
}
